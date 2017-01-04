﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Harmony
{
	public class PatchTools
	{
		public static unsafe void Detour(MethodInfo source, MethodInfo destination)
		{
			RuntimeHelpers.PrepareMethod(source.MethodHandle);
			RuntimeHelpers.PrepareMethod(destination.MethodHandle);

			if (IntPtr.Size == sizeof(Int64))
			{
				long Source_Base = source.MethodHandle.GetFunctionPointer().ToInt64();
				long Destination_Base = destination.MethodHandle.GetFunctionPointer().ToInt64();

				byte* Pointer_Raw_Source = (byte*)Source_Base;

				long* Pointer_Raw_Address = (long*)(Pointer_Raw_Source + 0x02);

				*(Pointer_Raw_Source + 0x00) = 0x48;
				*(Pointer_Raw_Source + 0x01) = 0xB8;
				*Pointer_Raw_Address = Destination_Base;
				*(Pointer_Raw_Source + 0x0A) = 0xFF;
				*(Pointer_Raw_Source + 0x0B) = 0xE0;
			}
			else
			{
				int Source_Base = source.MethodHandle.GetFunctionPointer().ToInt32();
				int Destination_Base = destination.MethodHandle.GetFunctionPointer().ToInt32();

				byte* Pointer_Raw_Source = (byte*)Source_Base;

				int* Pointer_Raw_Address = (int*)(Pointer_Raw_Source + 1);

				int offset = (Destination_Base - Source_Base) - 5;

				*Pointer_Raw_Source = 0xE9;
				*Pointer_Raw_Address = offset;
			}
		}

		public static MethodInfo GetPatchMethod<T>(Type patchType, string name, Type[] parameter)
		{
			var method = patchType.GetMethods(AccessTools.all)
				.FirstOrDefault(m => m.GetCustomAttributes(typeof(T), true).Count() > 0);
			if (method == null)
				method = patchType.GetMethod(name, AccessTools.all, null, parameter, null);
			return method;
		}

		public static void Patch(Type patchType, Type type, string methodName, Type[] paramTypes)
		{
			var original = type.GetMethod(methodName, AccessTools.all, null, paramTypes, null);
			if (original == null)
			{
				var paramList = "(" + string.Join(",", paramTypes.Select(t => t.FullName).ToArray()) + ")";
				throw new ArgumentException("No method found for " + type.FullName + "." + methodName + paramList);
			}

			var parameters = original.GetParameters();
			var prefixParams = new List<Type>();
			var postfixParams = new List<Type>();
			if (original.IsStatic == false)
			{
				prefixParams.Add(type);
				postfixParams.Add(type);
			}
			if (original.ReturnType != typeof(void))
			{
				var retRef = original.ReturnType.MakeByRefType();
				prefixParams.Add(retRef);
				postfixParams.Add(retRef);
			}
			parameters.ToList().ForEach(pi =>
			{
				var paramRef = pi.ParameterType.MakeByRefType();
				if (pi.IsOut == false) // prefix patches should not get out-parameters
					prefixParams.Add(paramRef);
				postfixParams.Add(paramRef);
			});

			var prepatch = GetPatchMethod<HarmonyPrefix>(patchType, "Prefix", prefixParams.ToArray());
			var postpatch = GetPatchMethod<HarmonyPostfix>(patchType, "Postfix", postfixParams.ToArray());
			if (prepatch == null && postpatch == null)
			{
				var prepatchStr = "Prefix(" + String.Join(", ", prefixParams.Select(p => p.FullName).ToArray()) + ")";
				var postpatchStr = "Postfix(" + String.Join(", ", postfixParams.Select(p => p.FullName).ToArray()) + ")";
				throw new MissingMethodException("No prefix/postfix patch for " + type.FullName + "." + methodName + "() found that matches " + prepatchStr + " or " + postpatchStr);
			}

			if (prepatch != null && prepatch.ReturnType != typeof(bool))
				throw new MissingMethodException("Prefix() must return bool (return true to execute original method)");
			if (postpatch != null && postpatch.ReturnType != typeof(void))
				throw new MissingMethodException("Postfix() must not return anything");

			PatchedMethod.Patch(original, prepatch, postpatch);
		}

		// Here we generate a wrapper method that has the same signature as the original or
		// the copy of the original. It will be recreated every time there is a change in
		// the list of prefix/postfixes and the original gets repatched to this wrapper.
		// This wrapper then calls all prefix/postfixes and also the copy of the original.
		// We cannot call the original here because the detour destroys it.
		//
		// Format of a prefix/postfix call is:
		// bool Prefix ([TYPE instance,] [ref TYPE result,], ref p1, ref p2, ref p3 ...)
		// void Postfix([TYPE instance,] [ref TYPE result,], ref p1, ref p2, ref p3 ...)
		// - "instance" only for non-static original methods
		// - "result" only for original methods that do not return void
		// - prefix will receive all parameters EXCEPT "out" parameters
		//
		//	static RTYPE Patch(TYPE instance, TYPE p1, TYPE p2, TYPE p3 ...)
		//	{
		//		object result = default(RTYPE);
		//
		//		bool run = true;
		//
		//		if (run)
		//			run = Prefix1(instance, ref result, ref p1, ref p2, ref p3 ...);
		//		if (run)
		//			run = Prefix2(instance, ref result, ref p1, ref p2, ref p3 ...);
		//		...
		//
		//		if (run)
		//			result = instance.Original(p1, p2, p3 ...);
		//
		//		Postfix1(instance, ref result, ref p1, ref p2, ref p3 ...);
		//		Postfix2(instance, ref result, ref p1, ref p2, ref p3 ...);
		//		...
		//
		//		return result;
		//	}
		//
		public static DynamicMethod CreatePatchWrapper(MethodInfo original, PatchedMethod patch)
		{
			var method = CreateDynamicMethod(original, "_wrapper");
			var g = method.GetILGenerator();

			var isInstance = original.IsStatic == false;
			var returnType = original.ReturnType;
			var hasReturnValue = returnType != typeof(void);
			var parameters = original.GetParameters();

			var resultType = hasReturnValue ? returnType : typeof(object);
			g.DeclareLocal(resultType); // v0 - result
			g.DeclareLocal(typeof(bool)); // v1 - run

			g.Emit(OpCodes.Nop);

			// ResultType result = [default value for ResultType];
			if (returnType.IsValueType && hasReturnValue)
				g.Emit(OpCodes.Ldc_I4, 0);
			else
				g.Emit(OpCodes.Ldnull);
			g.Emit(OpCodes.Stloc_0); // to v0

			// bool run = true;
			g.Emit(OpCodes.Ldc_I4, 1); // true
			g.Emit(OpCodes.Stloc_1); // to v1

			patch.GetPrefixPatches().ForEach(prepatch =>
			{
				var ifRunPrepatch = g.DefineLabel();

				// if (run)
				g.Emit(OpCodes.Ldloc_1); // v1
				g.Emit(OpCodes.Ldc_I4, 0); // false
				g.Emit(OpCodes.Ceq); // compare
				g.Emit(OpCodes.Brtrue, ifRunPrepatch); // jump to (A)

				// run = Prefix[n](instance, ref result, ref p1, ref p2, ref p3 ...);
				if (isInstance)
					g.Emit(OpCodes.Ldarg_0); // instance
				if (hasReturnValue)
					g.Emit(OpCodes.Ldloca_S, 0); // ref result
				for (int j = 0; j < parameters.Count(); j++)
				{
					if (parameters[j].IsOut) continue; // out parameter make no sense for prefix methods 
					var j2 = isInstance ? j + 1 : j;
					g.Emit(OpCodes.Ldarga_S, j2); // ref p[1..n]
				}
				g.Emit(OpCodes.Call, prepatch); // call prefix patch
				g.Emit(OpCodes.Stloc_1); // to v1

				g.MarkLabel(ifRunPrepatch); // (A)
			});

			// if (run)
			g.Emit(OpCodes.Ldloc_1); // v1
			g.Emit(OpCodes.Ldc_I4, 0); // false
			g.Emit(OpCodes.Ceq); // compare
			var ifRunOriginal = g.DefineLabel();
			g.Emit(OpCodes.Brtrue, ifRunOriginal); // jump to (B)

			// result = OriginalCopy(instance, p1, p2, p3 ...);
			if (isInstance)
				g.Emit(OpCodes.Ldarg_0); // instance
			for (int j = 0; j < parameters.Count(); j++)
			{
				var j2 = isInstance ? j + 1 : j;
				// p[1..n]
				if (j2 == 0) g.Emit(OpCodes.Ldarg_0);
				if (j2 == 1) g.Emit(OpCodes.Ldarg_1);
				if (j2 == 2) g.Emit(OpCodes.Ldarg_2);
				if (j2 == 3) g.Emit(OpCodes.Ldarg_3);
				if (j2 > 3) g.Emit(OpCodes.Ldarg_S, j2);
			}
			g.Emit(OpCodes.Call, patch.GetOriginalCopy()); // call copy of original
			if (hasReturnValue)
				g.Emit(OpCodes.Stloc_0); // to v0

			g.MarkLabel(ifRunOriginal); // (B)

			patch.GetPostfixPatches().ForEach(postpatch =>
			{
				// Postfix[n](instance, ref result, ref p1, ref p2, ref p3 ...);
				if (isInstance)
					g.Emit(OpCodes.Ldarg_0); // instance
				if (hasReturnValue)
					g.Emit(OpCodes.Ldloca_S, 0); // ref result
				for (int j = 0; j < parameters.Count(); j++)
				{
					var j2 = isInstance ? j + 1 : j;
					g.Emit(OpCodes.Ldarga_S, j2); // ref p[1..n]
				}
				g.Emit(OpCodes.Call, postpatch); // call prefix patch
			});

			if (hasReturnValue)
				g.Emit(OpCodes.Ldloc_0); // v0
			g.Emit(OpCodes.Ret); // v0

			return method;
		}

		public static DynamicMethod CreateMethodCopy(MethodInfo original)
		{
			var method = CreateDynamicMethod(original, "_original");
			var generator = method.GetILGenerator();
			original.CopyOpCodes(method.GetILGenerator());
			return method;
		}

		public static MethodInfo PrepareDynamicMethod(MethodInfo original, DynamicMethod dynamicMethod)
		{
			var n = dynamicMethod.GetParameters().Count();
			var delegateFactory = new DelegateTypeFactory();
			var type = delegateFactory.CreateDelegateType(original);
			return dynamicMethod.CreateDelegate(type).Method;
		}

		private static DynamicMethod CreateDynamicMethod(MethodInfo original, string suffix)
		{
			if (original == null) throw new ArgumentNullException("original");

			var hasThisAsFirstParameter = (original.IsStatic == false);
			var patchName = original.Name + suffix;

			var parameters = original.GetParameters();
			var result = parameters.Select(pi => pi.ParameterType).ToList();
			if (original.IsStatic == false)
				result.Insert(0, typeof(object));
			var paramTypes = result.ToArray();

			var method = new DynamicMethod(
				patchName,
				MethodAttributes.Public | (original.IsStatic ? MethodAttributes.Static : 0) /* original.Attributes */,
				CallingConventions.Standard /* original.CallingConvention */,
				original.ReturnType,
				paramTypes,
				original.DeclaringType,
				true
			);

			return method;
		}
	}
}