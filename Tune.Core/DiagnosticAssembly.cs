﻿using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Diagnostics.Runtime;
using Mono.Cecil;
using SharpDisasm;
using SharpDisasm.Translators;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tune.Core
{
    public class DiagnosticAssembly : IDisposable
    {
        private DiagnosticEngine engine;
        private MemoryStream dllStream;
        private MemoryStream pdbStream;
        private Assembly assembly;
        private string assemblyName;
        private ulong currentMethodAddress = 0;

        public DiagnosticAssembly(DiagnosticEngine engine, string assemblyName, CSharpCompilation compilation)
        {
            this.engine = engine;
            this.assemblyName = assemblyName;
            //UpdateLog($"Script compilation into assembly {assemblyName} in {level}.");
            this.dllStream = new MemoryStream();
            this.pdbStream = new MemoryStream();
            var emitResult = compilation.Emit(this.dllStream, this.pdbStream);
            if (!emitResult.Success)
            {
                var x = emitResult.Diagnostics;
                //UpdateLog($"Script compilation failed: {string.Join(Environment.NewLine, x.Select(d => d.ToString()))}.");
                throw new Exception();
            }
            //UpdateLog("Script compilation succeeded.");
            this.dllStream.Seek(0, SeekOrigin.Begin);
            this.assembly = Assembly.Load(this.dllStream.ToArray());
            //UpdateLog("Dynamic assembly loaded.");
        }

        public string Execute(string argument)
        {
            Type type = assembly.GetTypes().First();
            MethodInfo mi = type.GetMethods(BindingFlags.Instance | BindingFlags.Public).First();
            object obj = Activator.CreateInstance(type);
            //UpdateLog($"Object with type {type.FullName} and method {mi.Name} resolved.");

            object result = null;
            try
            {
                TextWriter programWriter = new StringWriter();
                Console.SetOut(programWriter);
                //UpdateLog($"Invoking method {mi.Name} with argument {tuple.Item2}");
                result = mi.Invoke(obj, new object[] { argument });
                //UpdateLog($"Script result: {result}");
                //UpdateLog("Script log:");
                //UpdateLog(programWriter.ToString(), printTime: false);
                return result.ToString();
            }
            catch (Exception e)
            {
                //UpdateLog($"Script execution failed: {e.ToString()}");
                return e.ToString();
            }
        }

        public string DumpIL()
        {
            TextWriter ilWriter = new StringWriter();
            var assemblyDefinition = AssemblyDefinition.ReadAssembly(this.dllStream);
            var ilOutput = new PlainTextOutput(ilWriter);
            var reflectionDisassembler = new ReflectionDisassembler(ilOutput, false, CancellationToken.None);
            reflectionDisassembler.WriteModuleContents(assemblyDefinition.MainModule);
            //UpdateLog("Dynamic assembly disassembled to IL.");
            //UpdateIL(ilWriter.ToString());
            return ilWriter.ToString();
        }

        public string DumpASM()
        {
            TextWriter asmWriter = new StringWriter();
            using (DataTarget target = DataTarget.AttachToProcess(Process.GetCurrentProcess().Id, 5000, AttachFlag.Passive))
            {
                foreach (ClrInfo clrInfo in target.ClrVersions)
                {
                    //UpdateLog("Found CLR Version:" + clrInfo.Version.ToString());

                    // This is the data needed to request the dac from the symbol server:
                    ModuleInfo dacInfo = clrInfo.DacInfo;
                    //UpdateLog($"Filesize:  {dacInfo.FileSize:X}");
                    //UpdateLog($"Timestamp: {dacInfo.TimeStamp:X}");
                    //UpdateLog($"Dac File:  {dacInfo.FileName}");

                    ClrRuntime runtime = target.ClrVersions.Single().CreateRuntime();
                    var appDomain = runtime.AppDomains[0];
                    var module = appDomain.Modules.LastOrDefault(m => m.AssemblyName != null && m.AssemblyName.StartsWith(assemblyName));
                    
                    asmWriter.WriteLine(
                        $"; {clrInfo.ModuleInfo.ToString()} ({clrInfo.Flavor} {clrInfo.Version})");
                    asmWriter.WriteLine(
                        $"; {clrInfo.DacInfo.FileName} ({clrInfo.DacInfo.TargetArchitecture} {clrInfo.DacInfo.Version})");
                    asmWriter.WriteLine();
                    foreach (var typeClr in module.EnumerateTypes())
                    {
                        asmWriter.WriteLine($"; Type {typeClr.Name}");

                        ClrHeap heap = runtime.Heap;
                        ClrType @object = heap.GetTypeByMethodTable(typeClr.MethodTable);

                        foreach (ClrMethod method in @object.Methods)
                        {
                            MethodCompilationType compileType = method.CompilationType;
                            ArchitectureMode mode = clrInfo.DacInfo.TargetArchitecture == Architecture.X86
                                ? ArchitectureMode.x86_32
                                : ArchitectureMode.x86_64;

                            this.currentMethodAddress = 0;
                            var translator = new IntelTranslator
                            {
                                SymbolResolver = (Instruction instruction, long addr, ref long offset) =>
                                    ResolveSymbol(runtime, instruction, addr, ref currentMethodAddress)
                            };

                            // This not work even ClrMd says opposite...
                            //ulong startAddress = method.NativeCode;
                            //ulong endAddress = method.ILOffsetMap.Select(entry => entry.EndAddress).Max();

                            DisassembleAndWrite(method, mode, translator, ref currentMethodAddress, asmWriter);
                            //UpdateLog($"Method {method.Name} disassembled to ASM.");
                            asmWriter.WriteLine();
                        }
                    }
                    //UpdateASM(asmWriter.ToString());
                    break;
                }
            }
            return asmWriter.ToString();
        }

        private void DisassembleAndWrite(ClrMethod method, ArchitectureMode architecture, Translator translator, ref ulong methodAddressRef, TextWriter writer)
        {
            writer.WriteLine(method.GetFullSignature());
            var info = FindNonEmptyHotColdInfo(method);
            if (info == null)
            {
                writer.WriteLine("    ; Failed to find HotColdInfo");
                return;
            }
            var methodAddress = info.HotStart;
            methodAddressRef = methodAddress;
            using (var disasm = new Disassembler(new IntPtr(unchecked((long)methodAddress)), (int)info.HotSize, architecture, methodAddress))
            {
                foreach (var instruction in disasm.Disassemble())
                {
                    writer.Write(String.Format("0x{0:X8}`{1:X8}:", (instruction.Offset >> 32) & 0xFFFFFFFF, instruction.Offset & 0xFFFFFFFF));
                    writer.Write("    L");
                    writer.Write((instruction.Offset - methodAddress).ToString("x4"));
                    writer.Write(": ");
                    writer.WriteLine(translator.Translate(instruction));
                }
            }
        }

        private HotColdRegions FindNonEmptyHotColdInfo(ClrMethod method)
        {
            // I can't really explain this, but it seems that some methods 
            // are present multiple times in the same type -- one compiled
            // and one not compiled. A bug in clrmd?
            if (method.HotColdInfo.HotSize > 0)
                return method.HotColdInfo;

            if (method.Type == null)
                return null;

            var methodSignature = method.GetFullSignature();
            foreach (var other in method.Type.Methods)
            {
                if (other.MetadataToken == method.MetadataToken && other.GetFullSignature() == methodSignature && other.HotColdInfo.HotSize > 0)
                    return other.HotColdInfo;
            }

            return null;
        }

        private string ResolveSymbol(ClrRuntime runtime, Instruction instruction, long addr, ref ulong currentMethodAddress)
        {
            var operand = instruction.Operands.Length > 0 ? instruction.Operands[0] : null;
            if (operand?.PtrOffset == 0)
            {
                var baseOffset = instruction.PC - currentMethodAddress;
                return $"L{baseOffset + operand.PtrSegment:x4}";
            }

            string signature = runtime.GetMethodByAddress(unchecked((ulong)addr))?.GetFullSignature();
            if (!string.IsNullOrWhiteSpace(signature))
                return signature;
            Symbol symbol = this.engine.ResolveNativeSymbol((ulong)addr);
            if (!string.IsNullOrWhiteSpace(symbol.MethodName))
                return symbol.ToString();
            return null;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    this.dllStream.Dispose();
                    this.pdbStream.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }

    public enum DiagnosticAssemblyMode
    {
        Release,
        Debug
    }

    public enum DiagnosticAssembyPlatform
    {
        x64,
        x86
    }
}