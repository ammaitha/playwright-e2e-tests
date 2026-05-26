using Framework.Core.Configuration;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;
using Serilog;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;
using Framework.Reports;

namespace Framework.Core.Driver;

/// <summary>
/// Thread-safe factory and registry for Selenium <see cref="OpenQA.Selenium.IWebDriver"/> instances.
/// 
/// BROWSER CONFIGURATION:
/// - Supported browsers: chrome (default), edge, firefox
/// - Set browser via environment variable: TestSettings__Browser=edge
/// - Or configure in appsettings.json: TestSettings:Browser
/// - Or set in .env file: TestSettings__Browser=firefox
/// 
/// HEADLESS MODE:
/// - Default: false (UI visible). Override via TestSettings__Headless=true
/// 
/// THREAD-SAFETY & PARALLEL EXECUTION:
/// - Browser instances are stored in ThreadLocal for thread-safe concurrent test execution
/// - Each test thread gets isolated WebDriver instance
/// - Call <see cref="InitializeDriver"/>, <see cref="GetDriver"/>, <see cref="QuitDriver"/> as needed
/// 
/// PRIORITY ORDER (highest to lowest):
/// 1. Environment variable (TestSettings__Browser, TestSettings__Headless)
/// 2. Configuration file (appsettings.json or appsettings.{TEST_ENV}.json)
/// 3. Default values (chrome, not headless)
/// </summary>
public static class DriverManager
{
    // ThreadLocal makes the design ready for future parallel test execution.
    private static readonly ThreadLocal<IWebDriver?> DriverThread = new();
    private static readonly object DriverBinarySetupLock = new();

    public static IWebDriver GetDriver()
    {
        return DriverThread.Value ?? throw new InvalidOperationException(
            "WebDriver is not initialized for the current thread. Call InitializeDriver first.");
    }

    /// <summary>
    /// Initializes the WebDriver with browser selection from config or environment variables.
    /// Priority order: Environment variable 'TestSettings__Browser' > config TestSettings:Browser > default chrome
    /// Supported: chrome, edge, firefox
    /// </summary>
    public static IWebDriver InitializeDriver()
    {
        if (DriverThread.Value is not null)
        {
            return DriverThread.Value;
        }

        var browser = (Environment.GetEnvironmentVariable("TestSettings__Browser")
            ?? ConfigManager.GetString("TestSettings:Browser")).ToLowerInvariant();

        try
        {
            var headless = bool.TryParse(Environment.GetEnvironmentVariable("TestSettings__Headless"), out var envHeadless)
                ? envHeadless
                : ConfigManager.GetBool("TestSettings:Headless");

            Log.Information("Initializing {Browser} WebDriver (Headless: {Headless})", browser, headless);

            DriverThread.Value = browser switch
            {
                "edge" => CreateEdgeDriver(headless),
                "firefox" => CreateFirefoxDriver(headless),
                _ => CreateChromeDriver(headless)
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebDriver. Ensure the browser is installed on your system.");
            throw;
        }

        var driver = DriverThread.Value;
        if (driver is null)
        {
            throw new InvalidOperationException("Failed to initialize WebDriver instance.");
        }

        driver.Manage().Window.Maximize();
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(ConfigManager.GetInt("TestSettings:PageLoadTimeoutSeconds"));
        driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(ConfigManager.GetInt("TestSettings:ScriptTimeoutSeconds"));
        RuntimeContext.BrowserName = browser;

        return driver;
    }

    public static void QuitDriver()
    {
        if (DriverThread.Value is null)
        {
            return;
        }

        try
        {
            DriverThread.Value.Quit();
            DriverThread.Value.Dispose();
        }
        finally
        {
            DriverThread.Value = null;
        }
    }

    /// <summary>
    /// Disposes the ThreadLocal WebDriver instance. Call this during test run cleanup
    /// to ensure proper resource cleanup, especially important for parallel test execution.
    /// </summary>
    public static void DisposeDriversForAllThreads()
    {
        DriverThread?.Dispose();
    }

    private static IWebDriver CreateChromeDriver(bool headless)
    {
        lock (DriverBinarySetupLock)
        {
            new WebDriverManager.DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);
        }

        var options = new ChromeOptions();
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");

        if (headless)
        {
            options.AddArgument("--headless=new");
        }

        return new ChromeDriver(options);
    }

    private static IWebDriver CreateEdgeDriver(bool headless)
    {
        lock (DriverBinarySetupLock)
        {
            new WebDriverManager.DriverManager().SetUpDriver(new EdgeConfig(), VersionResolveStrategy.MatchingBrowser);
        }

        var options = new EdgeOptions();
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");

        if (headless)
        {
            options.AddArgument("--headless=new");
        }

        return new EdgeDriver(options);
    }

    private static IWebDriver CreateFirefoxDriver(bool headless)
    {
        try
        {
            lock (DriverBinarySetupLock)
            {
                new WebDriverManager.DriverManager().SetUpDriver(new FirefoxConfig(), VersionResolveStrategy.MatchingBrowser);
            }
            Log.Information("WebDriverManager successfully configured Firefox driver");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WebDriverManager failed for Firefox. Attempting direct instantiation. Error: {Message}", ex.Message);
            Log.Information("Ensure Firefox is installed on your system. Download from: https://www.mozilla.org/firefox/");
        }

        var options = new FirefoxOptions();
        options.AddArgument("--width=1920");
        options.AddArgument("--height=1080");

        // Enable WebDriver BiDi so callers can register preload scripts that run on every
        // document load. Hybrid tests rely on this to keep API-seeded auth state pinned
        // across SPA-driven navigations on Firefox (CDP-style script injection is not
        // available on Firefox; BiDi is the cross-browser equivalent).
        options.UseWebSocketUrl = true;

        if (headless)
        {
            options.AddArgument("--headless");
        }

        Log.Information("Firefox WebDriver created successfully");
        return new FirefoxDriver(options);
    }
}