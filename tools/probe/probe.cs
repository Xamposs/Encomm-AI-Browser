using System;
using System.Linq;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace P;

class M
{
    public static int Main()
    {
        var t = typeof(CoreWebView2Environment);
        Console.WriteLine($"Assembly: {t.Assembly.GetName().FullName}");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "CreateAsync"))
        {
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"  {m.Name}({ps})");
        }
        return 0;
    }
}