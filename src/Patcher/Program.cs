using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Usage: Patcher input.dll output.dll");
string input = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(AmrPDF.Updater.UpdateManager).Assembly.Location)!);
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false });
var module = assembly.MainModule;
var appType = module.Types.FirstOrDefault(t => t.Name == "App" && t.Methods.Any(m => m.Name == "OnStartup"))
    ?? throw new InvalidOperationException("App.OnStartup was not found.");
var startup = appType.Methods.First(m => m.Name == "OnStartup" && m.HasBody);
if (!startup.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.DeclaringType.FullName == "AmrPDF.Updater.UpdateManager"))
{
    MethodInfo initialize = typeof(AmrPDF.Updater.UpdateManager).GetMethod(nameof(AmrPDF.Updater.UpdateManager.Initialize), BindingFlags.Public | BindingFlags.Static)!;
    MethodReference imported = module.ImportReference(initialize);
    startup.Body.GetILProcessor().InsertBefore(startup.Body.Instructions[0], Instruction.Create(OpCodes.Call, imported));
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
assembly.Write(output);
Console.WriteLine($"Patched {appType.FullName}.{startup.Name}");

