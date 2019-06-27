﻿using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq.Expressions;
using MonoMod.Utils;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;
using System.Runtime.InteropServices;
using System.IO;

#if NETSTANDARD
using TypeOrTypeInfo = System.Reflection.TypeInfo;
using static System.Reflection.IntrospectionExtensions;
#else
using TypeOrTypeInfo = System.Type;
#endif

namespace MonoMod.Utils {
    public static class ReflectionHelper {

        private static readonly Dictionary<string, Assembly> AssemblyCache = new Dictionary<string, Assembly>();
        private static readonly Dictionary<string, MemberInfo> ResolveReflectionCache = new Dictionary<string, MemberInfo>();

        private const BindingFlags _BindingFlagsAll = (BindingFlags) (-1);

#if NETSTANDARD1_X
        private static readonly Type t_AssemblyLoadContext =
            typeof(Assembly).GetTypeInfo().Assembly
            .GetType("System.Runtime.Loader.AssemblyLoadContext");
        private static readonly object _AssemblyLoadContext_Default =
            t_AssemblyLoadContext.GetProperty("Default").GetValue(null);
        private static readonly MethodInfo _AssemblyLoadContext_LoadFromStream =
            t_AssemblyLoadContext.GetMethod("LoadFromStream", new Type[] { typeof(Stream) });
#endif

        private static MemberInfo _Cache(MemberReference key, MemberInfo value) {
            if (key != null && value != null) {
                lock (ResolveReflectionCache) {
                    ResolveReflectionCache[key.FullName] = value;
                }
            }
            return value;
        }

        public static Assembly Load(ModuleDefinition module) {
            using (MemoryStream stream = new MemoryStream()) {
                module.Write(stream);
                stream.Seek(0, SeekOrigin.Begin);
                return Load(stream);
            }
        }

        public static Assembly Load(Stream stream) {
            Assembly asm;

#if NETSTANDARD1_X
            // System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(asmStream);
            asm = (Assembly) _AssemblyLoadContext_LoadFromStream.Invoke(_AssemblyLoadContext_Default, new object[] { stream });
#else

            if (stream is MemoryStream ms) {
                asm = Assembly.Load(ms.GetBuffer());
            } else {
                using (MemoryStream copy = new MemoryStream()) {

#if NETFRAMEWORK
                    byte[] buffer = new byte[4096];
                    int read;
                    while (0 < (read = stream.Read(buffer, 0, buffer.Length))) {
                        copy.Write(buffer, 0, read);
                    }
#else
                    stream.CopyTo(copy);
#endif

                    copy.Seek(0, SeekOrigin.Begin);
                    asm = Assembly.Load(copy.GetBuffer());
                }
            }
#endif

#if !NETSTANDARD1_X
            AppDomain.CurrentDomain.AssemblyResolve +=
                (s, e) => e.Name == asm.FullName ? asm : null;
#endif

            return asm;
        }

        public static Type ResolveReflection(this TypeReference mref)
            => (_ResolveReflection(mref, null) as TypeOrTypeInfo).AsType();
        public static MethodBase ResolveReflection(this MethodReference mref)
            => _ResolveReflection(mref, null) as MethodBase;
        public static FieldInfo ResolveReflection(this FieldReference mref)
            => _ResolveReflection(mref, null) as FieldInfo;
        public static PropertyInfo ResolveReflection(this PropertyReference mref)
            => _ResolveReflection(mref, null) as PropertyInfo;
        public static EventInfo ResolveReflection(this EventReference mref)
            => _ResolveReflection(mref, null) as EventInfo;

        public static MemberInfo ResolveReflection(this MemberReference mref)
            => _ResolveReflection(mref, null);

        private static MemberInfo _ResolveReflection(MemberReference mref, Module[] modules) {
            if (mref == null)
                return null;

            lock (ResolveReflectionCache) {
                if (ResolveReflectionCache.TryGetValue(mref.FullName, out MemberInfo cached) && cached != null)
                    return cached;
            }

            TypeOrTypeInfo type;

            // Special cases.
            if (mref is GenericParameter genParam) {
                // TODO: Handle GenericParameter in ResolveReflection.
                throw new NotSupportedException("ResolveReflection on GenericParameter currently not supported");
            }

            if (mref is MethodReference method && mref.DeclaringType is ArrayType) {
                // ArrayType holds special methods.
                type = _ResolveReflection(mref.DeclaringType, modules) as TypeOrTypeInfo;
                // ... but all of the methods have the same MetadataToken. We couldn't compare it anyway.

                string methodID = method.GetFindableID(withType: false);
                MethodBase found = 
                    type.AsType().GetMethods(_BindingFlagsAll).Cast<MethodBase>()
                    .Concat(type.AsType().GetConstructors(_BindingFlagsAll))
                    .FirstOrDefault(m => m.GetFindableID(withType: false) == methodID);
                if (found != null)
                    return _Cache(mref, found);
            }

            TypeReference tscope =
                mref.DeclaringType ??
                mref as TypeReference ??
                throw new ArgumentException("MemberReference hasn't got a DeclaringType / isn't a TypeReference in itself");

            if (modules == null) {
                string asmName;
                string moduleName;

                switch (tscope.Scope) {
                    case AssemblyNameReference asmNameRef:
                        asmName = asmNameRef.FullName;
                        moduleName = null;
                        break;

                    case ModuleDefinition moduleDef:
                        asmName = moduleDef.Assembly.FullName;
                        moduleName = moduleDef.Name;
                        break;

                    case ModuleReference moduleRef:
                        // TODO: Is this correct? It's what cecil itself is doing...
                        asmName = tscope.Module.Assembly.FullName;
                        moduleName = tscope.Module.Name;
                        break;

                    default:
                        throw new NotSupportedException($"Unsupported scope type {tscope.Scope.GetType().FullName}");
                }

                Assembly asm = null;
                lock (AssemblyCache) {
                    if (!AssemblyCache.TryGetValue(asmName, out asm)) {
#if !NETSTANDARD1_X
                        asm =
                            AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault(other => {
                                AssemblyName name = other.GetName();
                                return name.Name == asmName || name.FullName == asmName;
                            });
#endif
                        if (asm == null)
                            asm = Assembly.Load(new AssemblyName(asmName));
                        AssemblyCache[asmName] = asm;
                    }
                }
                modules = string.IsNullOrEmpty(moduleName) ? asm.GetModules() : new Module[] { asm.GetModule(moduleName) };
            }

            if (mref is TypeReference tref) {
                if (tref.FullName == "<Module>")
                    throw new ArgumentException("Type <Module> cannot be resolved to a runtime reflection type");

                if (mref is TypeSpecification ts) {
                    type = _ResolveReflection(ts.ElementType, null) as TypeOrTypeInfo;
                    if (type == null)
                        return null;

                    if (ts.IsByReference)
                        return ResolveReflectionCache[mref.FullName] = type.MakeByRefType().GetTypeInfo();

                    if (ts.IsPointer)
                        return ResolveReflectionCache[mref.FullName] = type.MakePointerType().GetTypeInfo();

                    if (ts.IsArray)
                        return ResolveReflectionCache[mref.FullName] = (ts as ArrayType).IsVector ? type.MakeArrayType().GetTypeInfo() : type.MakeArrayType((ts as ArrayType).Dimensions.Count).GetTypeInfo();

                    if (ts.IsGenericInstance)
                        return ResolveReflectionCache[mref.FullName] = type.MakeGenericType((ts as GenericInstanceType).GenericArguments.Select(arg => (_ResolveReflection(arg, null) as TypeOrTypeInfo).AsType()).ToArray()).GetTypeInfo();

                } else {
                    type = modules
                        .Select(module => module.GetType(mref.FullName.Replace("/", "+"), false, false).GetTypeInfo())
                        .FirstOrDefault(m => m != null);
#if !NETSTANDARD1_X
                    if (type == null)
                        type = modules
                            .Select(module => module.GetTypes().FirstOrDefault(m => mref.Is(m)).GetTypeInfo())
                            .FirstOrDefault(m => m != null);
#endif
                }

                return _Cache(mref, type);
            }

            bool typeless = mref.DeclaringType.FullName == "<Module>";

            MemberInfo member;

            if (mref is GenericInstanceMethod mrefGenMethod) {
                member = _ResolveReflection(mrefGenMethod.ElementMethod, modules);
                member = (member as MethodInfo)?.MakeGenericMethod(mrefGenMethod.GenericArguments.Select(arg => (_ResolveReflection(arg, null) as TypeOrTypeInfo).AsType()).ToArray());

            } else if (typeless) {
                if (mref is MethodReference)
                    member = modules
                        .Select(module => module.GetMethods(_BindingFlagsAll).FirstOrDefault(m => mref.Is(m)))
                        .FirstOrDefault(m => m != null);
                else if (mref is FieldReference)
                    member = modules
                        .Select(module => module.GetFields(_BindingFlagsAll).FirstOrDefault(m => mref.Is(m)))
                        .FirstOrDefault(m => m != null);
                else
                    throw new NotSupportedException($"Unsupported <Module> member type {mref.GetType().FullName}");

            } else {
                Type declType = (_ResolveReflection(mref.DeclaringType, modules) as TypeOrTypeInfo).AsType();

                if (mref is MethodReference)
                    member = declType
                        .GetMethods(_BindingFlagsAll).Cast<MethodBase>()
                        .Concat(declType.GetConstructors(_BindingFlagsAll))
                        .FirstOrDefault(m => mref.Is(m));
                else if (mref is FieldReference) 
                    member = declType
                        .GetFields(_BindingFlagsAll)
                        .FirstOrDefault(m => mref.Is(m));
                else
                    member = declType
                        .GetMembers(_BindingFlagsAll)
                        .FirstOrDefault(m => mref.Is(m));
            }

            return _Cache(mref, member);
        }

        public static SignatureHelper ResolveReflection(this CallSite csite, Module context)
            => ResolveReflectionSignature(csite, context);
        public static SignatureHelper ResolveReflectionSignature(this IMethodSignature csite, Module context) {
            SignatureHelper shelper;
            switch (csite.CallingConvention) {
#if !NETSTANDARD
                case MethodCallingConvention.C:
                    shelper = SignatureHelper.GetMethodSigHelper(context, CallingConvention.Cdecl, csite.ReturnType.ResolveReflection());
                    break;

                case MethodCallingConvention.StdCall:
                    shelper = SignatureHelper.GetMethodSigHelper(context, CallingConvention.StdCall, csite.ReturnType.ResolveReflection());
                    break;

                case MethodCallingConvention.ThisCall:
                    shelper = SignatureHelper.GetMethodSigHelper(context, CallingConvention.ThisCall, csite.ReturnType.ResolveReflection());
                    break;

                case MethodCallingConvention.FastCall:
                    shelper = SignatureHelper.GetMethodSigHelper(context, CallingConvention.FastCall, csite.ReturnType.ResolveReflection());
                    break;

                case MethodCallingConvention.VarArg:
                    shelper = SignatureHelper.GetMethodSigHelper(context, CallingConventions.VarArgs, csite.ReturnType.ResolveReflection());
                    break;

#else
                case MethodCallingConvention.C:
                case MethodCallingConvention.StdCall:
                case MethodCallingConvention.ThisCall:
                case MethodCallingConvention.FastCall:
                case MethodCallingConvention.VarArg:
                    throw new NotSupportedException("Unmanaged calling conventions for callsites not supported");

#endif

                default:
                    if (csite.ExplicitThis) {
                        shelper = SignatureHelper.GetMethodSigHelper(context, CallingConventions.ExplicitThis, csite.ReturnType.ResolveReflection());
                    } else {
                        shelper = SignatureHelper.GetMethodSigHelper(context, CallingConventions.Standard, csite.ReturnType.ResolveReflection());
                    }
                    break;
            }

            if (context != null) {
                List<Type> modReq = new List<Type>();
                List<Type> modOpt = new List<Type>();

                foreach (ParameterDefinition param in csite.Parameters) {
                    if (param.ParameterType.IsSentinel)
                        shelper.AddSentinel();

                    if (param.ParameterType.IsPinned) {
                        shelper.AddArgument(param.ParameterType.ResolveReflection(), true);
                        continue;
                    }

                    modOpt.Clear();
                    modReq.Clear();

                    for (
                        TypeReference paramTypeRef = param.ParameterType;
                        paramTypeRef is TypeSpecification paramTypeSpec;
                        paramTypeRef = paramTypeSpec.ElementType
                    ) {
                        switch (paramTypeRef) {
                            case RequiredModifierType paramTypeModReq:
                                modReq.Add(paramTypeModReq.ModifierType.ResolveReflection());
                                break;

                            case OptionalModifierType paramTypeOptReq:
                                modOpt.Add(paramTypeOptReq.ModifierType.ResolveReflection());
                                break;
                        }
                    }

                    shelper.AddArgument(param.ParameterType.ResolveReflection(), modReq.ToArray(), modOpt.ToArray());
                }

            } else {
                foreach (ParameterDefinition param in csite.Parameters) {
                    shelper.AddArgument(param.ParameterType.ResolveReflection());
                }
            }

            return shelper;
        }

    }
}
