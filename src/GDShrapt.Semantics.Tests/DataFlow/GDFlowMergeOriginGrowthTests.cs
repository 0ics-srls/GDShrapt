using System.Linq;
using GDShrapt.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.DataFlow;

/// <summary>
/// Issue LSAI#48: merging branch states duplicated every shared type origin (List.AddRange of the same
/// instances), so the origins of a variable doubled at each if/loop merge — 2^k after k branches — and
/// AnalyzeAll on a real project grew to ~135 GB. A merge must keep each origin once.
/// </summary>
[TestClass]
public class GDFlowMergeOriginGrowthTests
{
    private static readonly GDSemanticType Int = GDSemanticType.FromRuntimeTypeName("int");

    private static int OriginCount(GDFlowState state) =>
        state.GetVariableType("x")!.CurrentType.AllTrackedTypes.Count();

    private static GDFlowState WithOneOrigin()
    {
        var state = new GDFlowState();
        var origin = new GDTypeOrigin(GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred,
            new GDFlowLocation("probe.gd", 1, 1));
        state.SetVariableType("x", Int, origin);
        return state;
    }

    [TestMethod]
    public void SequentialIfMerges_KeepEachOriginOnce()
    {
        var state = WithOneOrigin();

        for (var i = 0; i < 16; i++)
        {
            // if <cond>: x narrowed in the branch (variable modified, origins kept); no else.
            var ifBranch = state.CreateChild();
            ifBranch.NarrowType("x", Int);
            state = GDFlowState.MergeBranches(ifBranch, null, state.CreateChild());
        }

        Assert.AreEqual(1, OriginCount(state), "the single assignment origin must survive 16 merges once");
    }

    [TestMethod]
    public void DistinctOriginsWithEqualContent_AreBothKept()
    {
        // Dedup is by identity (the shared instance), never by content: two assignments stay two.
        var parent = new GDFlowState();
        var ifBranch = parent.CreateChild();
        ifBranch.SetVariableType("x", Int, new GDTypeOrigin(GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, new GDFlowLocation("probe.gd", 5, 1)));
        var elseBranch = parent.CreateChild();
        elseBranch.SetVariableType("x", Int, new GDTypeOrigin(GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, new GDFlowLocation("probe.gd", 5, 1)));

        var merged = GDFlowState.MergeBranches(ifBranch, elseBranch, parent.CreateChild());

        Assert.AreEqual(2, OriginCount(merged));
    }

    [TestMethod]
    public void MergingAStateWithItsOwnClone_DoesNotDuplicateOrigins()
    {
        var parent = WithOneOrigin();
        var ifBranch = parent.CreateChild();
        ifBranch.NarrowType("x", Int);
        var elseBranch = parent.CreateChild();
        elseBranch.NarrowType("x", Int);

        var merged = GDFlowState.MergeBranches(ifBranch, elseBranch, parent.CreateChild());

        Assert.AreEqual(1, OriginCount(merged));
    }
}
