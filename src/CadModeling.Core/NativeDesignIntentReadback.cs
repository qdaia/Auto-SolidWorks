namespace CadModeling.Core;

/// <summary>Independent postconditions for native equation insertion; ordering is not identity.</summary>
public static class NativeDesignIntentReadback
{
    public static void VerifyInsertedEquation(IReadOnlyList<DesignEquationState> before,
        IReadOnlyList<DesignEquationState> after, int returnedIndex, CompiledDesignEquation requested)
    {
        if (returnedIndex < 0 || returnedIndex >= after.Count || after.Count != before.Count + 1)
            throw new InvalidOperationException("添加方程失败或库存数量不符：" + requested.Target);
        var current = after[returnedIndex];
        if (after.Count(e => DesignIntentContract.EquationTarget(e.Equation).Equals(requested.Target, StringComparison.OrdinalIgnoreCase)) != 1
            || DesignIntentContract.EquationTarget(current.Equation) != requested.Target
            || current.GlobalVariable != requested.GlobalVariable || current.Disabled
            || !current.AllConfigurations
            || DesignIntentContract.NormalizeEquation(current.Equation) != DesignIntentContract.NormalizeEquation(requested.Equation))
            throw new InvalidOperationException("添加方程返回的身份、定义或作用域不符：" + requested.Target);
        foreach (var prior in before)
            if (after.Count(e => e.GlobalVariable == prior.GlobalVariable && e.Disabled == prior.Disabled
                && e.AllConfigurations == prior.AllConfigurations
                && DesignIntentContract.NormalizeEquation(e.Equation) == DesignIntentContract.NormalizeEquation(prior.Equation)) != 1)
                throw new InvalidOperationException("添加方程改变了原有定义或作用域。");
    }

    // These native tree nodes expose a shared placeholder persistent ID and have no
    // suppressible modeling feature. Do not filter unknown types or generic *Folder names.
    public static bool IsPresentationTreeNode(string nativeType) => nativeType is
        "FavoriteFolder" or "HistoryFolder" or "SelectionSetFolder" or "SensorFolder"
        or "EnvFolder" or "InkMarkupFolder" or "MaterialFolder" or "ConfigCommentsFolder";
}
