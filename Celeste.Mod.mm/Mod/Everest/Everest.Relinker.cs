﻿using Celeste.Mod.Core;
using Celeste.Mod.Helpers;
using Ionic.Zip;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace Celeste.Mod {
    public static partial class Everest {
        /// <summary>
        /// Relink mods to point towards Celeste.exe and FNA / XNA properly at runtime.
        /// </summary>
        public static class Relinker {

            /// <summary>
            /// The hasher used by Relinker.
            /// </summary>
            public readonly static HashAlgorithm ChecksumHasher = MD5.Create();

            /// <summary>
            /// The current Celeste.exe's checksum.
            /// </summary>
            public static string GameChecksum { get; internal set; }

            internal readonly static Dictionary<string, ModuleDefinition> StaticRelinkModuleCache = new Dictionary<string, ModuleDefinition>() {
                { "MonoMod", ModuleDefinition.ReadModule(typeof(MonoModder).Assembly.Location, new ReaderParameters(ReadingMode.Immediate)) },
                { "Celeste", ModuleDefinition.ReadModule(typeof(Celeste).Assembly.Location, new ReaderParameters(ReadingMode.Immediate)) }
            };
            internal static bool RuntimeRulesParsed = false;

            private static Dictionary<string, ModuleDefinition> _SharedRelinkModuleMap;
            public static Dictionary<string, ModuleDefinition> SharedRelinkModuleMap {
                get {
                    if (_SharedRelinkModuleMap != null)
                        return _SharedRelinkModuleMap;

                    _SharedRelinkModuleMap = new Dictionary<string, ModuleDefinition>();
                    string[] entries = Directory.GetFiles(PathGame);
                    for (int i = 0; i < entries.Length; i++) {
                        string path = entries[i];
                        string name = Path.GetFileName(path);
                        string nameNeutral = name.Substring(0, Math.Max(0, name.Length - 4));
                        if (name.EndsWith(".mm.dll")) {
                            if (name.StartsWith("Celeste."))
                                _SharedRelinkModuleMap[nameNeutral] = StaticRelinkModuleCache["Celeste"];
                            else {
                                Logger.Log(LogLevel.Warn, "relinker", $"Found unknown {name}");
                                int dot = name.IndexOf('.');
                                if (dot < 0)
                                    continue;
                                string nameRelinkedNeutral = name.Substring(0, dot);
                                string nameRelinked = nameRelinkedNeutral + ".dll";
                                string pathRelinked = Path.Combine(Path.GetDirectoryName(path), nameRelinked);
                                if (!File.Exists(pathRelinked))
                                    continue;
                                if (!StaticRelinkModuleCache.TryGetValue(nameRelinkedNeutral, out ModuleDefinition relinked)) {
                                    relinked = ModuleDefinition.ReadModule(pathRelinked, new ReaderParameters(ReadingMode.Immediate));
                                    StaticRelinkModuleCache[nameRelinkedNeutral] = relinked;
                                }
                                Logger.Log(LogLevel.Verbose, "relinker", $"Remapped to {nameRelinked}");
                                _SharedRelinkModuleMap[nameNeutral] = relinked;
                            }
                        }
                    }
                    return _SharedRelinkModuleMap;
                }
            }

            private static Dictionary<string, object> _SharedRelinkMap;
            public static Dictionary<string, object> SharedRelinkMap {
                get {
                    if (_SharedRelinkMap != null)
                        return _SharedRelinkMap;

                    _SharedRelinkMap = new Dictionary<string, object>();

                    // Find our current XNA flavour and relink all types to it.
                    // This relinks mods from XNA to FNA and from FNA to XNA.

                    AssemblyName[] asmRefs = typeof(Celeste).Assembly.GetReferencedAssemblies();
                    for (int ari = 0; ari < asmRefs.Length; ari++) {
                        AssemblyName asmRef = asmRefs[ari];
                        // Ugly hardcoded supported framework list.
                        if (!asmRef.FullName.ToLowerInvariant().Contains("xna") &&
                            !asmRef.FullName.ToLowerInvariant().Contains("fna") &&
                            !asmRef.FullName.ToLowerInvariant().Contains("monogame") // Contains many differences - we should print a warning.
                        )
                            continue;
                        Assembly asm = Assembly.Load(asmRef);
                        ModuleDefinition module = ModuleDefinition.ReadModule(asm.Location, new ReaderParameters(ReadingMode.Immediate));
                        SharedRelinkModuleMap[asmRef.FullName] = SharedRelinkModuleMap[asmRef.Name] = module;
                        Type[] types = asm.GetExportedTypes();
                        for (int i = 0; i < types.Length; i++) {
                            Type type = types[i];
                            TypeDefinition typeDef = module.GetType(type.FullName) ?? module.GetType(type.FullName.Replace('+', '/'));
                            if (typeDef == null)
                                continue;
                            SharedRelinkMap[typeDef.FullName] = typeDef;
                        }
                    }

                    // Fix old mods depending on MonoModExt
                    SharedRelinkMap["MonoMod.Utils.MonoModExt"] = "MonoMod.Utils.Extensions";
                    SharedRelinkMap["System.String MonoMod.Utils.Extensions::GetFindableID(Mono.Cecil.MethodReference,System.String,System.String,System.Boolean,System.Boolean)"] =
                        new RelinkMapEntry("MonoMod.Utils.Extensions", "System.String GetID(Mono.Cecil.MethodReference,System.String,System.String,System.Boolean,System.Boolean)");
                    SharedRelinkMap["System.String MonoMod.Utils.Extensions::GetFindableID(System.Reflection.MethodBase,System.String,System.String,System.Boolean,System.Boolean,System.Boolean)"] =
                        new RelinkMapEntry("MonoMod.Utils.Extensions", "System.String GetID(System.Reflection.MethodBase,System.String,System.String,System.Boolean,System.Boolean,System.Boolean)");
                    SharedRelinkMap["Mono.Cecil.ModuleDefinition MonoMod.Utils.Extensions::ReadModule(System.String,Mono.Cecil.ReaderParameters)"] =
                        new RelinkMapEntry("Mono.Cecil.ModuleDefinition", "Mono.Cecil.ModuleDefinition ReadModule(System.String,Mono.Cecil.ReaderParameters)");

                    return _SharedRelinkMap;
                }
            }

            internal static bool SharedModder = true;
            private static MonoModder _Modder;
            public static MonoModder Modder {
                get {
                    if (_Modder != null)
                        return _Modder;

                    _Modder = new MonoModder() {
                        CleanupEnabled = false,
                        RelinkModuleMap = SharedRelinkModuleMap,
                        RelinkMap = SharedRelinkMap,
                        DependencyDirs = {
                            PathGame
                        },
                        ReaderParameters = {
                            SymbolReaderProvider = new RelinkerSymbolReaderProvider()
                        }
                    };

                    Modder.PostProcessors += ApplyModHackfixes;

                    return _Modder;
                }
                set {
                    _Modder = value;
                }
            }

            private static EverestModuleMetadata _Relinking;

            private static void ApplyModHackfixes(MonoModder modder) {
                // See Coroutine.ForceDelayedSwap for more info.
                // Older mods are built with this delay in mind, except for hooks.
                MethodInfo coroutineWrapper =
                    (_Relinking?.Dependencies?.Find(dep => dep.Name == CoreModule.Instance.Metadata.Name)
                    ?.Version ?? new Version(0, 0, 0, 0)) < new Version(1, 2563, 0) ?
                    typeof(CoroutineDelayHackfixHelper).GetMethod("Wrap") : null;

                if (coroutineWrapper == null)
                    return; // No hackfixes necessary.

                void CrawlMethod(MethodDefinition method) {
                    if (coroutineWrapper != null && method.HasBody && method.ReturnType.FullName == "System.Collections.IEnumerator") {
                        using (ILContext ctx = new ILContext(method)) {
                            ctx.Invoke(ctx => {
                                ILCursor c = new ILCursor(ctx);

                                // The Mono runtime hates executing this one simple trick.
                                /*
                                ILLabel skip = c.DefineLabel();
                                c.Emit(OpCodes.Br, skip);
                                ILLabel ret = c.MarkLabel();
                                c.Emit(OpCodes.Ldstr, method.GetID());
                                c.Emit(OpCodes.Call, coroutineWrapper);
                                c.Emit(OpCodes.Ret);
                                c.MarkLabel(skip);

                                while (c.TryGotoNext(i => i.MatchRet())) {
                                    c.Next.OpCode = OpCodes.Br;
                                    c.Next.Operand = ret;
                                }
                                */

                                // Why didn't I do this to begin with? -jade
                                while (c.TryGotoNext(i => i.MatchRet())) {
                                    c.Next.OpCode = OpCodes.Ldstr;
                                    c.Next.Operand = method.GetID();
                                    c.Index++;
                                    c.Emit(OpCodes.Call, coroutineWrapper);
                                    c.Emit(OpCodes.Ret);
                                }

                            });
                        }
                    }

                }

                void CrawlType(TypeDefinition type) {
                    foreach (MethodDefinition method in type.Methods)
                        CrawlMethod(method);

                    foreach (TypeDefinition nested in type.NestedTypes)
                        CrawlType(nested);
                }

                foreach (TypeDefinition type in modder.Module.Types)
                    CrawlType(type);
            }

            private static AssemblyDefinition OnRelinkerResolveFailure(object sender, AssemblyNameReference reference) {
                if (reference.FullName.ToLowerInvariant().Contains("fna") || reference.FullName.ToLowerInvariant().Contains("xna")) {
                    AssemblyName[] asmRefs = typeof(Celeste).Assembly.GetReferencedAssemblies();
                    for (int ari = 0; ari < asmRefs.Length; ari++) {
                        AssemblyName asmRef = asmRefs[ari];
                        if (!asmRef.FullName.ToLowerInvariant().Contains("xna") &&
                            !asmRef.FullName.ToLowerInvariant().Contains("fna") &&
                            !asmRef.FullName.ToLowerInvariant().Contains("monogame")
                        )
                            continue;
                        return ((DefaultAssemblyResolver) _Modder.AssemblyResolver).Resolve(AssemblyNameReference.Parse(asmRef.FullName));
                    }
                }

                return null;
            }

            /// <summary>
            /// Relink a .dll to point towards Celeste.exe and FNA / XNA properly at runtime, then load it.
            /// </summary>
            /// <param name="meta">The mod metadata, used for caching, among other things.</param>
            /// <param name="stream">The stream to read the .dll from.</param>
            /// <param name="depResolver">An optional dependency resolver.</param>
            /// <param name="checksumsExtra">Any optional checksums</param>
            /// <param name="prePatch">An optional step executed before patching, but after MonoMod has loaded the input assembly.</param>
            /// <returns>The loaded, relinked assembly.</returns>
            [Obsolete("Use the variant with an explicit assembly name instead.")]
            public static Assembly GetRelinkedAssembly(EverestModuleMetadata meta, Stream stream,
                MissingDependencyResolver depResolver = null, string[] checksumsExtra = null, Action<MonoModder> prePatch = null)
                => GetRelinkedAssembly(meta, Path.GetFileNameWithoutExtension(meta.DLL), stream, depResolver, checksumsExtra, prePatch);

            /// <summary>
            /// Relink a .dll to point towards Celeste.exe and FNA / XNA properly at runtime, then load it.
            /// </summary>
            /// <param name="meta">The mod metadata, used for caching, among other things.</param>
            /// <param name="asmname"></param>
            /// <param name="stream">The stream to read the .dll from.</param>
            /// <param name="depResolver">An optional dependency resolver.</param>
            /// <param name="checksumsExtra">Any optional checksums</param>
            /// <param name="prePatch">An optional step executed before patching, but after MonoMod has loaded the input assembly.</param>
            /// <returns>The loaded, relinked assembly.</returns>
            public static Assembly GetRelinkedAssembly(EverestModuleMetadata meta, string asmname, Stream stream,
                MissingDependencyResolver depResolver = null, string[] checksumsExtra = null, Action<MonoModder> prePatch = null) {
                if (!Flags.SupportRelinkingMods) {
                    Logger.Log(LogLevel.Warn, "relinker", "Relinker disabled!");
                    return null;
                }

                string cachedPath = GetCachedPath(meta, asmname);
                string cachedChecksumPath = cachedPath.Substring(0, cachedPath.Length - 4) + ".sum";

                string[] checksums = new string[2 + (checksumsExtra?.Length ?? 0)];
                if (GameChecksum == null)
                    GameChecksum = Everest.GetChecksum(Assembly.GetAssembly(typeof(Relinker)).Location).ToHexadecimalString();
                checksums[0] = GameChecksum;

                checksums[1] = Everest.GetChecksum(ref stream).ToHexadecimalString();

                if (checksumsExtra != null)
                    for (int i = 0; i < checksumsExtra.Length; i++) {
                        checksums[i + 2] = checksumsExtra[i];
                    }

                if (File.Exists(cachedPath) && File.Exists(cachedChecksumPath) &&
                    ChecksumsEqual(checksums, File.ReadAllLines(cachedChecksumPath))) {
                    Logger.Log(LogLevel.Verbose, "relinker", $"Loading cached assembly for {meta} - {asmname}");
                    try {
                        Assembly asm = Assembly.LoadFrom(cachedPath);
                        _RelinkedAssemblies.Add(asm);
                        return asm;
                    } catch (Exception e) {
                        Logger.Log(LogLevel.Warn, "relinker", $"Failed loading {meta} - {asmname}");
                        e.LogDetailed();
                        return null;
                    }
                }

                if (depResolver == null)
                    depResolver = GenerateModDependencyResolver(meta);

                AssemblyResolveEventHandler resolver = (s, r) => {
                    ModuleDefinition dep = depResolver(Modder, Modder.Module, r.Name, r.FullName);
                    if (dep != null)
                        return dep.Assembly;

                    return OnRelinkerResolveFailure(s, r);
                };

                bool temporaryASM = false;

                try {
                    _Relinking = meta;

                    MonoModder modder = Modder;

                    modder.Input = stream;
                    modder.OutputPath = cachedPath;
                    modder.MissingDependencyResolver = depResolver;

                    ((DefaultAssemblyResolver) modder.AssemblyResolver).ResolveFailure += resolver;

                    modder.ReaderParameters.SymbolStream = OpenStream(meta, out string symbolPath, meta.DLL.Substring(0, meta.DLL.Length - 4) + ".pdb", meta.DLL + ".mdb");
                    modder.ReaderParameters.ReadSymbols = modder.ReaderParameters.SymbolStream != null;
                    if (modder.ReaderParameters.SymbolReaderProvider != null &&
                        modder.ReaderParameters.SymbolReaderProvider is RelinkerSymbolReaderProvider) {
                        ((RelinkerSymbolReaderProvider) modder.ReaderParameters.SymbolReaderProvider).Format =
                            string.IsNullOrEmpty(symbolPath) ? DebugSymbolFormat.Auto :
                            symbolPath.EndsWith(".mdb") ? DebugSymbolFormat.MDB :
                            symbolPath.EndsWith(".pdb") ? DebugSymbolFormat.PDB :
                            DebugSymbolFormat.Auto;
                    }

                    try {
                        modder.ReaderParameters.ReadSymbols = true;
                        modder.Read();
                    } catch {
                        modder.ReaderParameters.SymbolStream?.Dispose();
                        modder.ReaderParameters.SymbolStream = null;
                        modder.ReaderParameters.ReadSymbols = false;
                        stream.Seek(0, SeekOrigin.Begin);
                        modder.Read();
                    }

                    if (modder.ReaderParameters.SymbolReaderProvider != null &&
                        modder.ReaderParameters.SymbolReaderProvider is RelinkerSymbolReaderProvider) {
                        ((RelinkerSymbolReaderProvider) modder.ReaderParameters.SymbolReaderProvider).Format = DebugSymbolFormat.Auto;
                    }

                    modder.MapDependencies();

                    if (!RuntimeRulesParsed) {
                        RuntimeRulesParsed = true;

                        InitMMSharedData();

                        string rulesPath = Path.Combine(
                            Path.GetDirectoryName(typeof(Celeste).Assembly.Location),
                            Path.GetFileNameWithoutExtension(typeof(Celeste).Assembly.Location) + ".Mod.mm.dll"
                        );
                        if (!File.Exists(rulesPath)) {
                            // Fallback if someone renamed Celeste.exe
                            rulesPath = Path.Combine(
                                Path.GetDirectoryName(typeof(Celeste).Assembly.Location),
                                "Celeste.Mod.mm.dll"
                            );
                        }
                        if (File.Exists(rulesPath)) {
                            ModuleDefinition rules = ModuleDefinition.ReadModule(rulesPath, new ReaderParameters(ReadingMode.Immediate));
                            modder.ParseRules(rules);
                            rules.Dispose(); // Is this safe?
                        }
                    }

                    prePatch?.Invoke(modder);

                    modder.ParseRules(modder.Module);

                    modder.AutoPatch();

                    ISymbolWriterProvider symbolWriterProvider = modder.WriterParameters.SymbolWriterProvider;

                    RetryWrite:
                    try {
                        modder.WriterParameters.SymbolWriterProvider = symbolWriterProvider;
                        modder.WriterParameters.WriteSymbols = true;
                        modder.Write();
                    } catch {
                        try {
                            modder.WriterParameters.SymbolWriterProvider = null;
                            modder.WriterParameters.WriteSymbols = false;
                            modder.Write();
                        } catch when (!temporaryASM) {
                            temporaryASM = true;
                            long stamp = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                            cachedPath = Path.Combine(Path.GetTempPath(), $"Everest.Relinked.{Path.GetFileNameWithoutExtension(cachedPath)}.{stamp}.dll");
                            modder.Module.Name += "." + stamp;
                            modder.Module.Assembly.Name.Name += "." + stamp;
                            modder.OutputPath = cachedPath;
                            modder.WriterParameters.WriteSymbols = true;
                            goto RetryWrite;
                        }
                    } finally {
                        modder.WriterParameters.SymbolWriterProvider = symbolWriterProvider;
                    }
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, "relinker", $"Failed relinking {meta} - {asmname}");
                    e.LogDetailed();
                    return null;
                } finally {
                    ((DefaultAssemblyResolver) Modder.AssemblyResolver).ResolveFailure -= resolver;
                    Modder.ReaderParameters.SymbolStream?.Dispose();
                    if (SharedModder) {
                        Modder.ClearCaches(moduleSpecific: true);
                        Modder.Module?.Dispose();
                        Modder.Module = null;

                    } else {
                        Modder.Dispose();
                        Modder = null;
                    }
                }

                if (File.Exists(cachedChecksumPath)) {
                    File.Delete(cachedChecksumPath);
                }
                if (!temporaryASM) {
                    File.WriteAllLines(cachedChecksumPath, checksums);
                }

                Logger.Log(LogLevel.Verbose, "relinker", $"Loading assembly for {meta} - {asmname}");
                try {
                    Assembly asm = Assembly.LoadFrom(cachedPath);
                    _RelinkedAssemblies.Add(asm);
                    return asm;
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, "relinker", $"Failed loading {meta} - {asmname}");
                    e.LogDetailed();
                    return null;
                }
            }

            private static MissingDependencyResolver GenerateModDependencyResolver(EverestModuleMetadata meta) {
                if (!string.IsNullOrEmpty(meta.PathArchive)) {
                    return (mod, main, name, fullName) => {
                        string path = name + ".dll";
                        if (!string.IsNullOrEmpty(meta.DLL))
                            path = Path.Combine(Path.GetDirectoryName(meta.DLL), path);
                        path = path.Replace('\\', '/');

                        using (ZipFile zip = new ZipFile(meta.PathArchive)) {
                            foreach (ZipEntry entry in zip.Entries) {
                                if (entry.FileName != path)
                                    continue;
                                using (MemoryStream stream = entry.ExtractStream())
                                    return ModuleDefinition.ReadModule(stream, mod.GenReaderParameters(false));
                            }
                        }
                        return null;
                    };
                }

                if (!string.IsNullOrEmpty(meta.PathDirectory)) {
                    return (mod, main, name, fullName) => {
                        string path = name + ".dll";
                        if (!string.IsNullOrEmpty(meta.DLL))
                            path = Path.Combine(Path.GetDirectoryName(meta.DLL), path);
                        if (!File.Exists(path))
                            path = Path.Combine(meta.PathDirectory, path);
                        if (!File.Exists(path))
                            return null;

                        return ModuleDefinition.ReadModule(path, mod.GenReaderParameters(false, path));
                    };
                }

                return null;
            }

            private static Stream OpenStream(EverestModuleMetadata meta, out string result, params string[] names) {
                if (!string.IsNullOrEmpty(meta.PathArchive)) {
                    using (ZipFile zip = new ZipFile(meta.PathArchive)) {
                        foreach (ZipEntry entry in zip.Entries) {
                            if (!names.Contains(entry.FileName))
                                continue;
                            result = entry.FileName;
                            return entry.ExtractStream();
                        }
                    }
                    result = null;
                    return null;
                }

                if (!string.IsNullOrEmpty(meta.PathDirectory)) {
                    foreach (string name in names) {
                        string path = name;
                        if (!File.Exists(path))
                            path = Path.Combine(meta.PathDirectory, name);
                        if (!File.Exists(path))
                            continue;
                        result = path;
                        return File.OpenRead(path);
                    }
                }

                result = null;
                return null;
            }

            /// <summary>
            /// Get the cached path of a given mod's relinked .dll
            /// </summary>
            /// <param name="meta">The mod metadata.</param>
            /// <returns>The full path to the cached relinked .dll</returns>
            [Obsolete("Use the variant with an explicit assembly name instead.")]
            public static string GetCachedPath(EverestModuleMetadata meta)
                => GetCachedPath(meta, Path.GetFileNameWithoutExtension(meta.DLL));

            /// <summary>
            /// Get the cached path of a given mod's relinked .dll
            /// </summary>
            /// <param name="meta">The mod metadata.</param>
            /// <param name="asmname"></param>
            /// <returns>The full path to the cached relinked .dll</returns>
            public static string GetCachedPath(EverestModuleMetadata meta, string asmname)
                => Path.Combine(Loader.PathCache, meta.Name + "." + asmname + ".dll");

            /// <summary>
            /// Get the checksum for a given mod's .dll or the containing .zip
            /// </summary>
            /// <param name="meta">The mod metadata.</param>
            /// <returns>A checksum.</returns>
            [Obsolete("Use meta.Hash instead.")]
            public static string GetChecksum(EverestModuleMetadata meta) {
                string path = meta.PathArchive;
                if (string.IsNullOrEmpty(path))
                    path = meta.DLL;
                return GetChecksum(path);
            }
            /// <summary>
            /// Get the checksum for a given file.
            /// </summary>
            /// <param name="path">The file path.</param>
            /// <returns>A checksum.</returns>
            [Obsolete("Use Everest.GetChecksum instead.")]
            public static string GetChecksum(string path) {
                using (FileStream fs = File.OpenRead(path))
                    return ChecksumHasher.ComputeHash(fs).ToHexadecimalString();
            }

            /// <summary>
            /// Determine if both checksum collections are equal.
            /// </summary>
            /// <param name="a">The first checksum array.</param>
            /// <param name="b">The second checksum array.</param>
            /// <returns>True if the contents of both arrays match, false otherwise.</returns>
            public static bool ChecksumsEqual(string[] a, string[] b) {
                if (a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i].Trim() != b[i].Trim())
                        return false;
                return true;
            }

            [PatchInitMMSharedData]
            private static void InitMMSharedData() {
                // This method is automatically filled via MonoModRules.
            }
            private static void SetMMSharedData(string key, bool value) {
                Modder.SharedData[key] = value;
            }

        }
    }
}
