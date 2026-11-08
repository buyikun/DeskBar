using System;
using System.IO;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

class Program
{
    static void Main(string[] args)
    {
        var decompiler = new CSharpDecompiler(@"D:\app\DeskBar\DeskBar.dll", new DecompilerSettings());
        foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions)
        {
            Console.WriteLine(type.FullName);
        }
    }
}
