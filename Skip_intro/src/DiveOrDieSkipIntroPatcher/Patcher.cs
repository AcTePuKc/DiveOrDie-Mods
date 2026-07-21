using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DiveOrDieSkipIntroPatcher;

public static class Patcher
{
    public static IEnumerable<string> TargetDLLs => new[] { "Assembly-CSharp.dll" };

    public static void Patch(AssemblyDefinition assembly)
    {
        var patchedIntro = PatchCutscenePlayer(assembly);
        var patchedSplash = PatchStartupSplash(assembly);
        var patchedDemoWelcome = PatchDemoWelcomePopup(assembly);
        Console.WriteLine($"[DiveOrDieSkipIntro] intro={patchedIntro}; splash={patchedSplash}; demoWelcome={patchedDemoWelcome}");
    }

    private static bool PatchDemoWelcomePopup(AssemblyDefinition assembly)
    {
        var type = FindType(assembly, "DemoWelcomePopup");
        var start = type?.Methods.FirstOrDefault(method => method.Name == "Start" && method.Parameters.Count == 0);
        if (start == null)
            return false;

        var body = new MethodBody(start);
        body.GetILProcessor().Append(body.GetILProcessor().Create(OpCodes.Ret));
        start.Body = body;
        return true;
    }

    private static bool PatchCutscenePlayer(AssemblyDefinition assembly)
    {
        var type = FindType(assembly, "CutscenePlayer");
        var original = type?.Methods.FirstOrDefault(candidate =>
            candidate.Name == "PlayCutscene" && candidate.Parameters.Count == 2);
        if (original == null)
            return false;

        var actionInvoke = typeof(Action).GetMethod(nameof(Action.Invoke));
        if (actionInvoke == null)
            return false;

        original.Name = "PlayCutsceneOriginal";
        var wrapper = new MethodDefinition(original.Name.Replace("Original", string.Empty),
            original.Attributes, original.ReturnType)
        {
            ImplAttributes = original.ImplAttributes,
            CallingConvention = original.CallingConvention
        };
        foreach (var parameter in original.Parameters)
            wrapper.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        type.Methods.Add(wrapper);

        foreach (var candidate in assembly.MainModule.Types.SelectMany(Flatten).SelectMany(candidate => candidate.Methods))
        {
            if (candidate == wrapper || !candidate.HasBody)
                continue;

            foreach (var instruction in candidate.Body.Instructions)
            {
                if (instruction.Operand is MethodReference reference &&
                    reference.Name == "PlayCutsceneOriginal" &&
                    reference.DeclaringType.FullName == type.FullName)
                    instruction.Operand = wrapper;
            }
        }

        var body = new MethodBody(wrapper);
        var il = body.GetILProcessor();
        var originalPath = il.Create(OpCodes.Nop);
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Bne_Un, originalPath));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Callvirt, assembly.MainModule.ImportReference(actionInvoke)));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(originalPath);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Call, original));
        il.Append(il.Create(OpCodes.Ret));
        wrapper.Body = body;
        return true;
    }

    private static bool PatchStartupSplash(AssemblyDefinition assembly)
    {
        var stateMachine = FindType(assembly, "<StartupSequence>d__13");
        var moveNext = stateMachine?.Methods.FirstOrDefault(method => method.Name == "MoveNext");
        if (moveNext?.Body == null)
            return false;

        var instructions = moveNext.Body.Instructions;
        var videoPlay = instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference reference &&
            reference.Name == "Play" &&
            reference.DeclaringType.FullName.Contains("UnityEngine.Video.VideoPlayer"));
        var audioPlay = instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference reference &&
            reference.Name == "Play" &&
            reference.DeclaringType.FullName.Contains("FMODUnity.StudioEventEmitter"));
        Console.WriteLine($"[DiveOrDieSkipIntro] splash probes: video={videoPlay != null}; audio={audioPlay != null}; stateMachine={stateMachine.FullName}");
        if (videoPlay == null || audioPlay == null)
            return false;

        NopReceiverAndCall(instructions, videoPlay);
        NopReceiverAndCall(instructions, audioPlay);

        var displayClass = stateMachine.DeclaringType.NestedTypes
            .FirstOrDefault(type => type.Name == "<>c__DisplayClass13_0");
        var splashPredicate = displayClass?.Methods
            .FirstOrDefault(method => method.Name == "<StartupSequence>b__1");
        Console.WriteLine($"[DiveOrDieSkipIntro] splash predicate found={splashPredicate != null}");
        if (splashPredicate?.Body == null)
            return false;

        ReplaceWithTrue(splashPredicate);

        var disclaimerPredicates = displayClass.Methods
            .Where(method => method.Name == "<StartupSequence>b__2" ||
                            method.Name == "<StartupSequence>b__3" ||
                            method.Name == "<StartupSequence>b__4")
            .ToArray();
        foreach (var predicate in disclaimerPredicates)
            ReplaceWithTrue(predicate);

        var activation = instructions.FirstOrDefault(instruction =>
            instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference reference &&
            reference.Name == "SetActive" &&
            instruction.Previous?.OpCode == OpCodes.Ldc_I4_1 &&
            instruction.Previous.Previous?.Operand?.ToString()?.Contains("Component::get_gameObject") == true);
        if (activation != null)
            NopRange(instructions, instructions.IndexOf(activation) - 4, instructions.IndexOf(activation));

        var disclaimerStart = instructions.FirstOrDefault(instruction => instruction.Offset == 0x01DD);
        var disclaimerEnd = instructions.FirstOrDefault(instruction => instruction.Offset == 0x0336);
        var disclaimerSkipped = disclaimerStart != null && disclaimerEnd != null;
        if (disclaimerSkipped)
        {
            disclaimerStart.OpCode = OpCodes.Br;
            disclaimerStart.Operand = disclaimerEnd;
        }

        Console.WriteLine($"[DiveOrDieSkipIntro] autosave disclaimer predicates={disclaimerPredicates.Length}; hidden={activation != null}; skipped={disclaimerSkipped}");
        return true;
    }

    private static void ReplaceWithTrue(MethodDefinition method)
    {
        var body = new MethodBody(method);
        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ret));
        method.Body = body;
    }

    private static void NopRange(Mono.Collections.Generic.Collection<Instruction> instructions, int start, int end)
    {
        for (var index = Math.Max(0, start); index <= end && index < instructions.Count; index++)
        {
            instructions[index].OpCode = OpCodes.Nop;
            instructions[index].Operand = null;
        }
    }

    private static void NopReceiverAndCall(Mono.Collections.Generic.Collection<Instruction> instructions,
        Instruction call)
    {
        var index = instructions.IndexOf(call);
        NopRange(instructions, index - 2, index);
    }

    private static TypeDefinition FindType(AssemblyDefinition assembly, string name)
    {
        return assembly.MainModule.Types
            .SelectMany(Flatten)
            .FirstOrDefault(type => type.Name == name || type.FullName == name);
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        {
            foreach (var child in Flatten(nested))
                yield return child;
        }
    }
}
