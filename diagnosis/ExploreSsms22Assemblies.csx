// SSMS 22 Assembly Explorer Script
// Run this in SSMS 22's Roslyn scripting environment or as a standalone script

using System;
using System.Reflection;
using System.IO;

var ssmsPath = @"D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE";

Console.WriteLine("=== Exploring SSMS 22 Assemblies ===\n");

// Key assemblies to explore
var assembliesToExplore = new[]
{
    "Microsoft.SqlServer.Management.Sdk.SqlStudio.dll",
    "Microsoft.SqlServer.SqlTools.VSIntegration.dll",
    "ObjectExplorer.dll",
    "ConnectionDlg.dll",
    "Microsoft.SqlServer.Management.ConnectionUI.dll",
    "Microsoft.SqlServer.Management.ConnectionUI.Dialog.dll"
};

foreach (var asmName in assembliesToExplore)
{
    var path = Path.Combine(ssmsPath, asmName);
    if (!File.Exists(path))
    {
        Console.WriteLine($"❌ NOT FOUND: {asmName}");
        continue;
    }

    Console.WriteLine($"\n=== {asmName} ===");
    try
    {
        var asm = Assembly.LoadFrom(path);
        
        // Find service-related types
        var types = asm.GetTypes()
            .Where(t => t.IsPublic && (
                t.Name.Contains("Service") ||
                t.Name.Contains("Provider") ||
                t.Name.Contains("Manager") ||
                t.Name.Contains("Cache") ||
                t.Name.Contains("Dialog") ||
                t.Name.Contains("Connection")
            ))
            .OrderBy(t => t.Name);

        foreach (var type in types.Take(30))
        {
            Console.WriteLine($"  {type.FullName}");
            
            // Show key properties for service types
            if (type.Name.Contains("Service") || type.Name.Contains("Cache"))
            {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(p => p.Name.Contains("Instance") || p.Name.Contains("Current") || p.Name.Contains("Default"));
                
                foreach (var prop in props)
                {
                    Console.WriteLine($"    ↳ {prop.Name}: {prop.PropertyType.Name}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error: {ex.Message}");
    }
}

Console.WriteLine("\n=== Done ===");
