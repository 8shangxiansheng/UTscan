using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Mock;
using UTscan.Services;
using UTscan.UI.Forms;

namespace UTscan;

static class Program
{
    private const string HardwareConfigFile = "hardware.json";

    // ── DI 组合根 ──
    // R5：非静态 HardwareConfigService 实例，供静态兼容方法委托与 MainForm 注入。
    private static IHardwareConfigService? s_configService;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // H-9：注册全局异常处理器记录故障
        Application.ThreadException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[Program] UI 线程异常: {e.Exception?.Message}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[Program] 未处理异常: {e.ExceptionObject}");

        // 软件更新：启动时若发现待处理更新，拉起交换脚本
        if (UpdateService.HasPendingUpdate())
        {
            try
            {
                var swap = Path.Combine(AppContext.BaseDirectory, UpdateService.SwapScriptName);
                if (File.Exists(swap))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + swap + "\"",
                        UseShellExecute = true,
                        CreateNoWindow = false
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Update] 拉起交换脚本失败: {ex.Message}");
            }
        }

        // ── R1 组合根：装配服务容器 ──
        var configService = new HardwareConfigService();
        s_configService = configService;
        var config = configService.LoadHardwareConfig();

        var services = new ServiceCollection();
        ConfigureServices(services, configService, config);
        using var provider = services.BuildServiceProvider();

        // ── 登录 ──
        var authService = provider.GetRequiredService<AuthService>();
        MainForm? mainForm = null;

        try
        {
            using var loginForm = new LoginForm(authService);
            if (loginForm.ShowDialog() == DialogResult.OK)
            {
                mainForm = provider.GetRequiredService<MainForm>();
                Application.Run(mainForm);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Program] 不可恢复异常: {ex}");
            try
            {
                MessageBox.Show($"程序发生不可恢复错误：{ex.Message}", "严重错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
        finally
        {
            if (mainForm != null)
            {
                try { mainForm.ShutdownCompletion.Wait(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Program] 等待 MainForm 关闭异常: {ex.Message}"); }
            }

            // H-9：finally 按 DPR → DAQ → ZMC 顺序释放
            try { provider.GetRequiredService<IPulseGenerator>().Dispose(); }
            catch { }
            try { provider.GetRequiredService<IDataAcquisition>().Dispose(); }
            catch { }
            try { provider.GetRequiredService<IMotionController>().Dispose(); }
            catch { }
        }
    }

    /// <summary>
    /// R1：DI 组合根配置。
    /// 注册全部服务与硬件实现（useMock 条件分支），MainForm 经容器解析。
    /// </summary>
    private static void ConfigureServices(IServiceCollection services,
        IHardwareConfigService configService, ConnectionConfig config)
    {
        // ── 配置 ──
        services.AddSingleton(configService);
        services.AddSingleton(config);

        // ── 硬件（useMock 条件分支） ──
        if (config.UseMock)
        {
            services.AddSingleton<IMotionController, MockMotionController>();
            services.AddSingleton<IDataAcquisition, MockDaqCard>();
            services.AddSingleton<IPulseGenerator, MockPulseGenerator>();
        }
        else
        {
            services.AddSingleton<IMotionController, UTscan.Hardware.Zmc.ZmcMotionController>();
            services.AddSingleton<IDataAcquisition, UTscan.Hardware.Daq.SpectrumDaqCard>();
            services.AddSingleton<IPulseGenerator, UTscan.Hardware.PulseGen.Dpr500Controller>();

            // 采样率单位防护：低于 9 MS/s 回退 100 MHz
            if (config.SampleRate < 9_000_000f)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[装配] hardware.json 采样率 {config.SampleRate} Hz 低于 M3i.3242 下限 9 MHz，回退 100 MHz");
                config.SampleRate = 100_000_000f;
            }
        }

        // ── 服务 ──
        services.AddSingleton<AuthService>();
        services.AddSingleton<IScanEngine, ScanService>();
        // LoginForm 不注册容器（手动创建 with using 生命周期）
        services.AddSingleton<MainForm>();
    }

    // ═══════════════════════════════════════════════════════════
    //  静态兼容方法（P5 待清理：测试依赖调用，内部委托给 HardwareConfigService；
    //  未初始化时回退临时实例，保持无状态可测性）
    // ═══════════════════════════════════════════════════════════

    internal static ConnectionConfig? TryLoadHardwareConfigFile(string path)
        => (s_configService ?? new HardwareConfigService()).TryLoadConfigFile(path);

    public static void SaveHardwareConfigSampleParams(int sampleRate, int sampleCount)
    {
        var svc = s_configService ?? new HardwareConfigService();
        svc.SaveSampleParams(sampleRate, sampleCount);
    }

    public static void SaveHardwareConfigSampleParams(int sampleRate, int sampleCount, string path)
    {
        var svc = s_configService ?? new HardwareConfigService();
        svc.SaveSampleParams(sampleRate, sampleCount, path);
    }

    /// <summary>实际加载的 hardware.json 绝对路径（供 UI 显示配置来源）</summary>
    public static string ConfigSourcePath => s_configService?.ConfigSourcePath ?? "";

    // ═══════════════════════════════════════════════════════════
    //  版本信息（软件更新方案：version.json 随发布包提供，程序启动时读取）
    // ═══════════════════════════════════════════════════════════

    internal sealed class VersionInfo
    {
        public string Version { get; set; } = "1.0.0";
        public string Build { get; set; } = "";
        public string Date { get; set; } = "";
    }

    private static readonly VersionInfo s_versionInfo = LoadVersionInfo();

    public static string VersionText { get; } = FormatVersion(s_versionInfo);
    public static string FullVersionText { get; } = FormatFullVersion(s_versionInfo);

    internal static VersionInfo LoadVersionInfo(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "version.json");
        try
        {
            if (!File.Exists(path)) return new VersionInfo();
            using var fs = File.OpenRead(path);
            var v = JsonSerializer.Deserialize<VersionInfo>(fs, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
            if (v is null || string.IsNullOrWhiteSpace(v.Version)) return new VersionInfo();
            return v;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[版本] 读取 {path} 失败（{ex.Message}），回退默认版本");
            return new VersionInfo();
        }
    }

    private static string FormatVersion(VersionInfo v)
        => string.IsNullOrWhiteSpace(v.Build) ? $"v{v.Version}" : $"v{v.Version} ({v.Build})";

    private static string FormatFullVersion(VersionInfo v)
        => string.IsNullOrWhiteSpace(v.Date)
            ? FormatVersion(v)
            : $"{FormatVersion(v)} · {v.Date}";
}