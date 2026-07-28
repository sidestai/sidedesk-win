using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace SideDesk.Services.Plugins;

/// <summary>
/// 插件编译结果
/// </summary>
public class PluginBuildResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public List<PluginBuildError> Errors { get; set; } = new();
    public List<PluginBuildError> Warnings { get; set; } = new();
}

public class PluginBuildError
{
    public string Id { get; set; } = "";
    public string Message { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Severity { get; set; } = "error"; // error / warning
}

/// <summary>
/// 插件编译请求
/// </summary>
public class PluginBuildRequest
{
    /// <summary>插件 ID</summary>
    public string PluginId { get; set; } = "";
    /// <summary>插件名称</summary>
    public string PluginName { get; set; } = "";
    /// <summary>C# 源代码（可包含多个文件，以 --- 分隔或单文件）</summary>
    public string Code { get; set; } = "";
    /// <summary>XAML 内容（可选，编译为 BAML 嵌入 DLL）</summary>
    public string? Xaml { get; set; } = "";
    /// <summary>plugin.json 配置内容</summary>
    public string? ConfigJson { get; set; } = "";
    /// <summary>插件类全名（命名空间.类名）</summary>
    public string PluginClass { get; set; } = "";
}

/// <summary>
/// 插件本地编译服务
/// 使用 Roslyn 在运行时编译用户编写的 C# 插件代码为 DLL
/// </summary>
public class PluginBuildService
{
    private static readonly Lazy<PluginBuildService> _instance = new(() => new PluginBuildService());
    public static PluginBuildService Instance => _instance.Value;

    /// <summary>插件开发临时输出目录（不直接写入 plugins 目录，保存时导出 zip）</summary>
    public string PluginsOutputDir { get; }

    private PluginBuildService()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        PluginsOutputDir = Path.Combine(exeDir, "plugin_dev");
        if (!Directory.Exists(PluginsOutputDir))
            Directory.CreateDirectory(PluginsOutputDir);
    }

    /// <summary>
    /// 编译插件代码为 DLL 并输出到 plugins 目录
    /// </summary>
    public PluginBuildResult Build(PluginBuildRequest request)
    {
        var result = new PluginBuildResult();

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            result.Errors.Add(new PluginBuildError
            {
                Message = "代码不能为空",
                Severity = "error"
            });
            return result;
        }

        try
        {
            // 解析语法树
            var syntaxTree = CSharpSyntaxTree.ParseText(request.Code,
                new CSharpParseOptions(LanguageVersion.Latest));

            // 收集引用程序集
            var references = GetMetadataReferences();

            // 确定程序集名称
            var assemblyName = string.IsNullOrEmpty(request.PluginId)
                ? $"Plugin_{Guid.NewGuid():N}"
                : request.PluginId.Replace("-", "_").Replace(" ", "_");

            // 创建编译
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithOptimizationLevel(OptimizationLevel.Release)
                    .WithNullableContextOptions(NullableContextOptions.Enable));

            // 编译到内存
            using var ms = new MemoryStream();
            EmitResult emitResult = compilation.Emit(ms);

            // 收集诊断信息
            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    result.Errors.Add(new PluginBuildError
                    {
                        Id = diag.Id,
                        Message = diag.GetMessage(),
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        Severity = "error"
                    });
                }
                else if (diag.Severity == DiagnosticSeverity.Warning)
                {
                    var lineSpan = diag.Location.GetLineSpan();
                    result.Warnings.Add(new PluginBuildError
                    {
                        Id = diag.Id,
                        Message = diag.GetMessage(),
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        Severity = "warning"
                    });
                }
            }

            if (!emitResult.Success)
            {
                result.Success = false;
                return result;
            }

            // 如果有松散 XAML，先校验再写入
            if (!string.IsNullOrWhiteSpace(request.Xaml))
            {
                var xamlError = ValidateXaml(request.Xaml);
                if (xamlError != null)
                {
                    result.Errors.Add(xamlError);
                    result.Success = false;
                    return result;
                }
            }

            // 输出到 plugins 目录
            var pluginDir = Path.Combine(PluginsOutputDir, assemblyName);
            if (!Directory.Exists(pluginDir))
                Directory.CreateDirectory(pluginDir);

            var dllPath = Path.Combine(pluginDir, $"{assemblyName}.dll");
            WriteFileWithRetry(dllPath, ms.ToArray());

            // 写入 plugin.json
            var configJson = request.ConfigJson;
            if (string.IsNullOrWhiteSpace(configJson))
            {
                configJson = GenerateDefaultConfig(request, assemblyName);
            }
            File.WriteAllText(Path.Combine(pluginDir, "plugin.json"), configJson, Encoding.UTF8);

            // 如果有松散 XAML，写入 plugin.json 中引用的文件名
            if (!string.IsNullOrWhiteSpace(request.Xaml))
            {
                // 从 configJson 解析 xaml 字段作为文件名，默认 ui.xaml
                var xamlFileName = "ui.xaml";
                if (!string.IsNullOrWhiteSpace(configJson))
                {
                    try
                    {
                        var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfig>(configJson);
                        if (!string.IsNullOrEmpty(cfg?.Xaml))
                            xamlFileName = cfg.Xaml;
                    }
                    catch { /* 解析失败用默认值 */ }
                }
                File.WriteAllText(Path.Combine(pluginDir, xamlFileName), request.Xaml, Encoding.UTF8);
            }

            // 保存 C# 源码（供下次打开编辑器时恢复）
            File.WriteAllText(Path.Combine(pluginDir, $"{assemblyName}.cs"), request.Code, Encoding.UTF8);

            result.Success = true;
            result.OutputPath = dllPath;
            Debug.WriteLine($"[PluginBuild] 编译成功: {dllPath}");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(new PluginBuildError
            {
                Message = $"编译异常: {ex.Message}",
                Severity = "error"
            });
        }

        return result;
    }

    /// <summary>
    /// 校验松散 XAML 是否可被 XamlReader 解析（模拟运行时加载）
    /// 返回 null 表示校验通过，否则返回错误信息
    /// </summary>
    private PluginBuildError? ValidateXaml(string xaml)
    {
        try
        {
            // 自动补全缺失的 xmlns:x 声明（避免 "'x' is an undeclared prefix" 误报）
            var xamlToValidate = xaml;
            if (!xaml.Contains("xmlns:x"))
            {
                // 在根元素的第一个 xmlns 后插入 xmlns:x
                var insertIdx = xaml.IndexOf("xmlns", StringComparison.Ordinal);
                if (insertIdx >= 0)
                {
                    // 找到第一个 xmlns="..." 的结束位置
                    var quoteStart = xaml.IndexOf('"', insertIdx);
                    var quoteEnd = xaml.IndexOf('"', quoteStart + 1);
                    if (quoteEnd > 0)
                    {
                        xamlToValidate = xaml.Insert(quoteEnd + 1,
                            "\n            xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
                    }
                }
            }

            // XamlReader.Parse 必须在 STA 线程上执行
            Exception? parseError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    System.Windows.Markup.XamlReader.Parse(xamlToValidate);
                }
                catch (Exception ex)
                {
                    parseError = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (parseError != null)
            {
                var msg = parseError.Message;
                int line = 0, col = 0;
                // 尝试从异常消息提取行号
                var lineMatch = System.Text.RegularExpressions.Regex.Match(msg, @"行号为\s*“(\d+)”");
                if (lineMatch.Success) line = int.Parse(lineMatch.Groups[1].Value);
                var colMatch = System.Text.RegularExpressions.Regex.Match(msg, @"行位置为\s*“(\d+)”");
                if (colMatch.Success) col = int.Parse(colMatch.Groups[1].Value);

                return new PluginBuildError
                {
                    Id = "XAML",
                    Message = $"XAML 校验失败: {msg}",
                    Line = line,
                    Column = col,
                    Severity = "error"
                };
            }
            return null;
        }
        catch (Exception ex)
        {
            return new PluginBuildError
            {
                Id = "XAML",
                Message = $"XAML 校验异常: {ex.Message}",
                Severity = "error"
            };
        }
    }

    /// <summary>
    /// 带重试的文件写入（解决 DLL 文件锁未立即释放的问题）
    /// </summary>
    private static void WriteFileWithRetry(string path, byte[] data, int maxRetries = 5)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                File.WriteAllBytes(path, data);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Debug.WriteLine($"[PluginBuild] 文件被占用，重试 ({i + 1}/{maxRetries}): {path}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// 获取编译所需的元数据引用（宿主所有已加载程序集 + 基础框架）
    /// </summary>
    private List<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>();

        // 基础运行时引用
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? [];
        foreach (var asm in trustedAssemblies)
        {
            if (File.Exists(asm))
                references.Add(MetadataReference.CreateFromFile(asm));
        }

        // 宿主程序集（包含 ISideDeskPlugin 接口）
        var hostAssembly = Assembly.GetExecutingAssembly();
        if (!string.IsNullOrEmpty(hostAssembly.Location))
            references.Add(MetadataReference.CreateFromFile(hostAssembly.Location));

        // WPF 相关程序集
        var wpfAssemblies = new[]
        {
            "PresentationFramework", "PresentationCore", "WindowsBase",
            "System.Xaml", "System.ObjectModel", "System.ComponentModel"
        };
        foreach (var name in wpfAssemblies)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
            if (asm != null && !string.IsNullOrEmpty(asm.Location))
                references.Add(MetadataReference.CreateFromFile(asm.Location));
        }

        return references;
    }

    /// <summary>
    /// 生成默认的 plugin.json 配置
    /// </summary>
    private string GenerateDefaultConfig(PluginBuildRequest request, string assemblyName)
    {
        return $$"""
        {
          "id": "{{request.PluginId}}",
          "name": "{{request.PluginName}}",
          "version": "1.0.0",
          "description": "用户自定义插件",
          "author": "User",
          "entry": "{{assemblyName}}.dll",
          "pluginClass": "{{request.PluginClass}}",
          "xaml": null,
          "icon": "🧩",
          "enabled": true
        }
        """;
    }
}
