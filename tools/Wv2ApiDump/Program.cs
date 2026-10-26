// 反射列出主工程上下文中 WebView2 的真实 API 表面。
// 起因：CoreWebView2Environment.CreateAsync 在 WinUI3（winmd 投影）里的重载
//       与 net462/WinForms 不同，猜参数名/参数个数连续报 CS1739 / CS1501。
//       一次列全，省得反复试。
using System;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static void Main()
    {
        var asm = typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment).Assembly;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("程序集: " + asm.GetName().Name + "  ver=" + asm.GetName().Version);
        Console.WriteLine("位置: " + asm.Location);
        Console.WriteLine();

        var t = typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment);
        Console.WriteLine("=== CoreWebView2Environment 全部静态方法 ===");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                           .OrderBy(x => x.Name))
        {
            if (m.Name != "CreateAsync" && m.Name != "GetAvailableBrowserVersionString") continue;
            var ps = string.Join(", ", m.GetParameters().Select(p =>
            {
                var def = p.HasDefaultValue ? " = 默认" : "";
                return $"{p.ParameterType.Name} {p.Name}{def}";
            }));
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({ps})");
        }

        Console.WriteLine();
        Console.WriteLine("=== SetVirtualHostNameToFolderMapping 重载 ===");
        var ctrl = typeof(Microsoft.UI.Xaml.Controls.WebView2);
        Console.WriteLine("  WebView2 控件类型: " + ctrl.FullName);
        Console.WriteLine("  程序集: " + ctrl.Assembly.GetName().Name);
        foreach (var m in ctrl.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .Where(x => x.Name.Contains("VirtualHost")))
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({ps})");
        }

        Console.WriteLine();
        Console.WriteLine("=== WebMessageReceived 事件 ===");
        var core = typeof(Microsoft.Web.WebView2.Core.CoreWebView2);
        foreach (var e in core.GetEvents(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine("  CoreWebView2." + e.Name + " : " + e.EventHandlerType?.Name);
        foreach (var e in ctrl.GetEvents(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine("  WebView2控件." + e.Name + " : " + e.EventHandlerType?.Name);

        Console.WriteLine();
        Console.WriteLine("=== NavigationCompleted / ProcessFailed 的 args 属性 ===");
        foreach (var tn in new[]{
            "CoreWebView2NavigationCompletedEventArgs",
            "CoreWebView2ProcessFailedEventArgs",
            "CoreWebView2ProcessFailedKind" })
        {
            var ty = typeof(Microsoft.Web.WebView2.Core.CoreWebView2).Assembly
                .GetTypes().FirstOrDefault(x => x.Name == tn
                    && x.Namespace == "Microsoft.Web.WebView2.Core");
            if (ty == null) { Console.WriteLine("  [缺] " + tn); continue; }
            Console.WriteLine("  " + ty.FullName);
            foreach (var p in ty.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .OrderBy(x => x.Name))
                Console.WriteLine("      " + p.PropertyType.Name + " " + p.Name);
            if (ty.IsEnum)
                foreach (var n in Enum.GetNames(ty))
                    Console.WriteLine("      enum: " + n);
        }

        Console.WriteLine();
        Console.WriteLine("=== WebMessageReceivedEventArgs 全部成员 ===");
        var argsTy = typeof(Microsoft.Web.WebView2.Core.CoreWebView2).Assembly
            .GetTypes().FirstOrDefault(x => x.Name == "CoreWebView2WebMessageReceivedEventArgs"
                && x.Namespace == "Microsoft.Web.WebView2.Core");
        if (argsTy == null) Console.WriteLine("  [缺] CoreWebView2WebMessageReceivedEventArgs");
        else
        {
            Console.WriteLine("  " + argsTy.FullName);
            foreach (var p in argsTy.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .OrderBy(x => x.Name))
                Console.WriteLine("      属性 " + p.PropertyType.Name + " " + p.Name);
            foreach (var m in argsTy.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .Where(x => !x.IsSpecialName)
                                .OrderBy(x => x.Name))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Console.WriteLine("      方法 " + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
            }
            // 返回类型是不是可空引用/装箱型：TryGetWebMessageAsString 在 WinRT 投影下
            // 返回 IReference<String>（CsWinRT 投影成 string?），net462 版返回 string。
            var tryGet = argsTy.GetMethod("TryGetWebMessageAsString");
            if (tryGet != null)
                Console.WriteLine("      TryGetWebMessageAsString 返回类型全名: " + tryGet.ReturnType.FullName);
        }

        Console.WriteLine();
        Console.WriteLine("=== 关键方法存在性 ===");
        var web = typeof(Microsoft.UI.Xaml.Controls.WebView2);
        void Has(Type ty, string name, params string[] argTypes)
        {
            var found = ty.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name == name &&
                    m.GetParameters().Select(p => p.ParameterType.Name)
                     .SequenceEqual(argTypes));
            Console.WriteLine($"  {(found ? "有" : "无")}  {ty.Name}.{name}({string.Join(",", argTypes)})");
        }
        Has(web, "EnsureCoreWebView2Async");
        Has(core, "PostWebMessageAsString", "String");
        Has(core, "PostWebMessageAsJson", "String");
        Has(core, "ExecuteScriptAsync", "String");
        Has(core, "AddScriptToExecuteOnDocumentCreatedAsync", "String");
    }
}
