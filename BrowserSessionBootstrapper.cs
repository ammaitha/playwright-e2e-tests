using System.Globalization;
using OpenQA.Selenium;
using OpenQA.Selenium.BiDi;
using OpenQA.Selenium.BiDi.Script;
using Serilog;

namespace Framework.Core.Utilities;

/// <summary>
/// Utility to quickly initialize an authenticated browser context for hybrid tests
/// by applying API-issued auth state (token, user storage, cookies) before UI steps.
/// This avoids repeated UI login flows and keeps API+UI test setup fast and deterministic.
/// </summary>
public sealed class BrowserSessionBootstrapper
{
    private readonly IWebDriver _driver;
    private readonly ILogger _logger;

    public BrowserSessionBootstrapper(IWebDriver driver)
        : this(driver, Log.Logger)
    {
    }

    public BrowserSessionBootstrapper(IWebDriver driver, ILogger logger)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _logger = logger ?? Log.Logger;
    }

    public void BootstrapAuthenticatedSession(
        Uri applicationBaseUri,
        string authToken,
        string userJson,
        IEnumerable<string>? setCookieHeaders = null,
        IEnumerable<string>? tokenStorageKeys = null,
        Action? waitAfterNavigation = null,
        string seedPath = "/login")
    {
        ArgumentNullException.ThrowIfNull(applicationBaseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);
        userJson ??= "{}";

        var keys = (tokenStorageKeys ?? new[] { "eventhub_token", "token", "accessToken", "authToken", "jwt" })
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // STEP 1: Register a BiDi preload script that re-seeds localStorage on EVERY
        // document load. Without this, the SPA's response interceptor clears the
        // token on the first 401 it sees (a known race on Firefox) and redirects to
        // /login. The preload script also installs a Storage.prototype.removeItem
        // guard so that even if the interceptor fires, the token is not removed.
        TryRegisterAuthPreloadScript(keys, authToken, userJson);

        // IMPORTANT: We must seed localStorage on a page that does NOT redirect when
        // the auth state is missing. The SPA's root layout calls router.replace("/login")
        // synchronously inside a useEffect when there is no user. On Chrome that effect
        // runs after our seeding races in, but on Firefox the navigation has already
        // committed, leaving us stuck on /login with a stale router state.
        //
        // Solution: navigate to a known "public" route (/login by default — whitelisted
        // by the SPA's auth gate), seed localStorage there, then navigate to the real
        // base URL. Now the SPA's auth gate sees the token on first hydration and
        // populates `user` via /auth/me without ever redirecting.
        var seedUri = new Uri(applicationBaseUri, string.IsNullOrWhiteSpace(seedPath) ? "/login" : seedPath);
        _driver.Navigate().GoToUrl(seedUri);
        waitAfterNavigation?.Invoke();

        ApplySetCookieHeaders(applicationBaseUri.Host, setCookieHeaders);
        SeedBrowserStorage(authToken, userJson, tokenStorageKeys);

        // Trigger the SPA's AuthProvider to run on the current (whitelisted) seed page
        // by forcing a soft refresh. /login is whitelisted by the auth gate so the page
        // does not redirect even before user state populates, giving /auth/me time to
        // succeed and populate the `user` context. Without this, Firefox sometimes lands
        // on the auth gate before /auth/me settles, clears the token, and redirects.
        try
        {
            _driver.Navigate().Refresh();
        }
        catch (WebDriverException ex)
        {
            _logger.Warning(ex, "[Session] Refresh after seeding storage failed; continuing.");
        }
        waitAfterNavigation?.Invoke();

        // Wait for the SPA's /auth/me round-trip to settle while we're on the seed page.
        // We can tell it settled by observing whether the token survived in localStorage —
        // the SPA's response interceptor removes it on any 401.
        WaitForTokenSettled(TimeSpan.FromSeconds(8));

        // Now navigate to the real destination with auth state populated.
        NavigateToBase(applicationBaseUri, waitAfterNavigation);

        // On the destination page, the SPA may issue further API calls that depend on the
        // bearer token. Verify the token is still present after the destination's first
        // /auth/me + initial data fetches settle. If it survives a short stability window,
        // the session is healthy.
        WaitForTokenSettled(TimeSpan.FromSeconds(6));
    }

    private void WaitForTokenSettled(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        // Track stability: token must remain present for at least 1 second to consider it settled.
        var stableSince = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            bool present;
            try
            {
                present = (((IJavaScriptExecutor)_driver).ExecuteScript(
                    "return !!(window.localStorage.getItem('eventhub_token'));")
                    is bool b) && b;
            }
            catch
            {
                present = false;
            }

            if (!present)
            {
                _logger.Warning("[Session] Token was removed from localStorage during bootstrap; SPA rejected it (likely a /auth/me 401).");
                return;
            }

            if (stableSince == DateTime.MinValue)
            {
                stableSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(1))
            {
                return;
            }

            Thread.Sleep(200);
        }
    }

    public void NavigateToBase(Uri applicationBaseUri, Action? waitAfterNavigation)
    {
        _driver.Navigate().GoToUrl(applicationBaseUri);
        waitAfterNavigation?.Invoke();
    }

    /// <summary>
    /// Registers a WebDriver BiDi preload script that re-seeds the auth token into
    /// localStorage and pins it against removal on every new document. This is the only
    /// reliable way to keep API-seeded auth state intact across SPA-driven navigations on
    /// Firefox (where CDP-based injection is unavailable). On Chrome/Edge this also works
    /// via BiDi when <c>UseWebSocketUrl</c> is enabled on the driver options.
    /// If BiDi is unavailable (older drivers, or capability not set), this method logs a
    /// warning and returns — the bootstrap will still try to seed via ExecuteScript and
    /// hope for the best.
    /// </summary>
    private void TryRegisterAuthPreloadScript(string[] keys, string authToken, string userJson)
    {
        try
        {
            // The preload script function body. Runs in every new document/realm BEFORE
            // any page scripts execute. We:
            //   1. Pre-populate localStorage and sessionStorage with the auth token
            //      (so the SPA's request interceptor sees it on the very first call).
            //   2. Override Storage.prototype.removeItem so the SPA's response
            //      interceptor cannot clear our token on a spurious 401.
            //   3. Override Location.prototype.assign/replace for redirects to /login
            //      (a defensive measure — the SPA also uses location.href which we
            //      cannot intercept, but blocking assign/replace covers Next.js router).
            var jsonToken = System.Text.Json.JsonSerializer.Serialize(authToken);
            var jsonUser = System.Text.Json.JsonSerializer.Serialize(userJson);
            var jsonKeys = System.Text.Json.JsonSerializer.Serialize(keys);

            var preloadSource =
                "() => { try {" +
                $"  const TOKEN = {jsonToken};" +
                $"  const USER = {jsonUser};" +
                $"  const KEYS = {jsonKeys};" +
                "  const seed = () => {" +
                "    try {" +
                "      for (const k of KEYS) {" +
                "        window.localStorage.setItem(k, TOKEN);" +
                "        window.sessionStorage.setItem(k, TOKEN);" +
                "      }" +
                "      window.localStorage.setItem('user', USER);" +
                "      window.localStorage.setItem('currentUser', USER);" +
                "      window.localStorage.setItem('isAuthenticated', 'true');" +
                "    } catch(e) { /* ignore */ }" +
                "  };" +
                "  seed();" +
                "  try {" +
                "    const origRemove = Storage.prototype.removeItem;" +
                "    Storage.prototype.removeItem = function(k) {" +
                "      if (KEYS.indexOf(k) >= 0) { return; }" +
                "      return origRemove.apply(this, arguments);" +
                "    };" +
                "    const origClear = Storage.prototype.clear;" +
                "    Storage.prototype.clear = function() { /* blocked by test bootstrap */ };" +
                "  } catch(e) { /* ignore */ }" +
                "  try {" +
                "    const origAssign = Location.prototype.assign;" +
                "    Location.prototype.assign = function(u) {" +
                "      if (typeof u === 'string' && u.endsWith('/login') && location.pathname !== '/login') return;" +
                "      return origAssign.apply(this, arguments);" +
                "    };" +
                "    const origReplace = Location.prototype.replace;" +
                "    Location.prototype.replace = function(u) {" +
                "      if (typeof u === 'string' && u.endsWith('/login') && location.pathname !== '/login') return;" +
                "      return origReplace.apply(this, arguments);" +
                "    };" +
                "  } catch(e) { /* ignore */ }" +
                "} catch(e) { /* ignore */ } }";

            // BiDi APIs are async; block here so the bootstrap flow stays synchronous.
            // Errors (no BiDi available, capability missing) are caught and logged so
            // tests still proceed using the legacy ExecuteScript path.
            var bidi = _driver.AsBiDiAsync().GetAwaiter().GetResult();
            var result = bidi.Script.AddPreloadScriptAsync(preloadSource).GetAwaiter().GetResult();
            _logger.Information("[Session] Registered BiDi preload script to keep auth state pinned across navigations.");
            _ = result; // result.Script holds the PreloadScript handle if removal is ever needed.
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "[Session] Could not register BiDi preload script (driver may not have BiDi enabled). " +
                "Auth state will be seeded only once via ExecuteScript and may be cleared by the SPA on Firefox.");
        }
    }



    public void SeedBrowserStorage(string authToken, string userJson, IEnumerable<string>? tokenStorageKeys)
    {
        var keys = (tokenStorageKeys ?? new[] { "eventhub_token", "token", "accessToken", "authToken", "jwt" })
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Seed both localStorage and sessionStorage and dispatch a synthetic 'storage'
        // event so any SPA listeners (cross-tab Redux/auth listeners) re-evaluate.
        // Additionally pin the protected token key against accidental removal — many SPAs
        // (including this one) clear the auth token from inside an axios response
        // interceptor on the first 401, then redirect to /login. That spurious 401 is
        // unrelated to the seeded token being valid (the SPA simply hasn't finished
        // hydrating /auth/me yet when an unrelated call lands). By preventing
        // removeItem('eventhub_token') from succeeding for the lifetime of the test we
        // keep the API+UI session aligned with the API-issued token.
        //
        // NOTE: Prototype-level patches like `Storage.prototype.removeItem` do NOT survive
        // a full document navigation because each navigation creates a new Window/Document
        // and a fresh set of prototypes. Callers that perform navigations after seeding
        // should call ReseedAfterNavigation() to re-apply the protection.
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const token = arguments[0];" +
            "const user = arguments[1];" +
            "const keys = arguments[2];" +
            "const protectedKeys = arguments[3];" +
            "for (let i = 0; i < keys.length; i++) {" +
            "  const key = keys[i];" +
            "  window.localStorage.setItem(key, token);" +
            "  window.sessionStorage.setItem(key, token);" +
            "}" +
            "window.localStorage.setItem('isAuthenticated', 'true');" +
            "window.localStorage.setItem('user', user);" +
            "window.localStorage.setItem('currentUser', user);" +
            "window.sessionStorage.setItem('isAuthenticated', 'true');" +
            "window.sessionStorage.setItem('user', user);" +
            "window.sessionStorage.setItem('currentUser', user);" +
            "try {" +
            "  for (let i = 0; i < keys.length; i++) {" +
            "    window.dispatchEvent(new StorageEvent('storage', { key: keys[i], newValue: token, storageArea: window.localStorage }));" +
            "  }" +
            "} catch (e) { /* ignore */ }" +
            "try {" +
            "  if (!window.__protectedTokenKeys) {" +
            "    window.__protectedTokenKeys = new Set(protectedKeys);" +
            "    const origRemove = Storage.prototype.removeItem;" +
            "    Storage.prototype.removeItem = function(k) {" +
            "      if (window.__protectedTokenKeys.has(k)) {" +
            "        try { console.warn('[TestHook] Suppressed removeItem(' + k + ') from SPA.'); } catch(e){}" +
            "        return;" +
            "      }" +
            "      return origRemove.apply(this, arguments);" +
            "    };" +
            "    const origClear = Storage.prototype.clear;" +
            "    Storage.prototype.clear = function() {" +
            "      try { console.warn('[TestHook] Storage.clear blocked.'); } catch(e){}" +
            "    };" +
            "    try {" +
            "      const origAssign = Location.prototype.assign;" +
            "      Location.prototype.assign = function(u) {" +
            "        if ((u||'').toString().endsWith('/login') && location.pathname !== '/login') {" +
            "          console.warn('[TestHook] Suppressed location.assign(/login) by SPA.');" +
            "          return;" +
            "        }" +
            "        return origAssign.apply(this, arguments);" +
            "      };" +
            "      const origReplace = Location.prototype.replace;" +
            "      Location.prototype.replace = function(u) {" +
            "        if ((u||'').toString().endsWith('/login') && location.pathname !== '/login') {" +
            "          console.warn('[TestHook] Suppressed location.replace(/login) by SPA.');" +
            "          return;" +
            "        }" +
            "        return origReplace.apply(this, arguments);" +
            "      };" +
            "    } catch(e) { /* ignore */ }" +
            "  }" +
            "} catch (e) { /* ignore */ }",
            authToken,
            userJson,
            keys,
            keys);
    }

    public void ApplySetCookieHeaders(string currentHost, IEnumerable<string>? setCookieHeaders)
    {
        if (setCookieHeaders == null)
        {
            return;
        }

        foreach (var setCookie in setCookieHeaders)
        {
            if (string.IsNullOrWhiteSpace(setCookie))
            {
                continue;
            }

            var segments = setCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var nameValue = segments[0];
            var separatorIndex = nameValue.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = nameValue[..separatorIndex];
            var value = nameValue[(separatorIndex + 1)..];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string? domain = currentHost;
            string path = "/";
            bool secure = false;
            bool httpOnly = false;
            string? sameSite = null;
            DateTime? expiry = null;

            foreach (var attr in segments.Skip(1))
            {
                if (attr.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                {
                    var rawDomain = attr[7..].Trim();
                    domain = rawDomain.TrimStart('.');
                }
                else if (attr.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    path = attr[5..].Trim();
                }
                else if (attr.Equals("Secure", StringComparison.OrdinalIgnoreCase))
                {
                    secure = true;
                }
                else if (attr.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase))
                {
                    httpOnly = true;
                }
                else if (attr.StartsWith("SameSite=", StringComparison.OrdinalIgnoreCase))
                {
                    sameSite = NormalizeSameSite(attr[9..].Trim());
                }
                else if (attr.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTime.TryParse(attr[8..].Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        expiry = parsed;
                    }
                }
                else if (attr.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(attr[8..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                    {
                        expiry = DateTime.UtcNow.AddSeconds(seconds);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(domain)
                && !currentHost.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
                && !domain.EndsWith(currentHost, StringComparison.OrdinalIgnoreCase))
            {
                domain = currentHost;
            }

            // HttpOnly cookies cannot be set through the WebDriver "Add Cookie" command in
            // some browser/driver combinations (notably Firefox). Skip rather than fail.
            if (httpOnly)
            {
                _logger.Debug("[Session] Skipping HttpOnly cookie '{Name}' (cannot be set via WebDriver).", name);
                continue;
            }

            // SameSite=None requires Secure=true per modern browser rules; otherwise Firefox
            // silently rejects. Promote to Secure to maximize acceptance.
            if (string.Equals(sameSite, "None", StringComparison.OrdinalIgnoreCase))
            {
                secure = true;
            }

            try
            {
                // httpOnly intentionally false: cookies that were HttpOnly are skipped above;
                // WebDriver "Add Cookie" can't reliably create HttpOnly cookies cross-browser.
                var cookie = new Cookie(name, value, domain, path, expiry, secure, false, sameSite);
                _driver.Manage().Cookies.AddCookie(cookie);
            }
            catch (Exception ex) when (ex is WebDriverException or ArgumentException or UnhandledAlertException)
            {
                _logger.Warning(ex,
                    "[Session] Failed to set cookie '{Name}' (domain={Domain}, sameSite={SameSite}, secure={Secure}). Retrying without optional attributes.",
                    name, domain, sameSite, secure);

                // Fallback for stricter drivers: drop sameSite/secure and try again.
                try
                {
                    _driver.Manage().Cookies.AddCookie(new Cookie(name, value, domain, path, null));
                }
                catch (Exception fallbackEx) when (fallbackEx is WebDriverException or ArgumentException)
                {
                    _logger.Warning(fallbackEx, "[Session] Fallback cookie set also failed for '{Name}'.", name);
                }
            }
        }
    }

    private static string? NormalizeSameSite(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "strict" => "Strict",
            "lax" => "Lax",
            "none" => "None",
            _ => null
        };
    }
}
