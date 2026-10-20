using System.Linq;
using GDShrapt.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GDShrapt.Semantics.Tests.DataFlow;

/// <summary>
/// LSAI#48 allocation cuts: a cloned flow type shares its history lists until one side mutates (copy-on-write),
/// and Godot member lookups are cached. Both must stay observably identical to the copying/rebuilding versions.
/// </summary>
[TestClass]
public class GDFlowAllocationTests
{
    private static readonly GDSemanticType Int = GDSemanticType.FromRuntimeTypeName("int");
    private static readonly GDSemanticType Str = GDSemanticType.FromRuntimeTypeName("String");
    private static readonly GDFlowLocation At = new("probe.gd", 1, 1);

    private static GDFlowVariableType WithHistory()
    {
        var type = new GDFlowVariableType();
        type.RecordAssignment(Int, GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, 1, 1);
        type.AddNarrowing(new GDNarrowingConstraint(GDNarrowingKind.IsCheck, Int, At));
        return type;
    }

    [TestMethod]
    public void Clone_MutatingTheCopy_LeavesTheOriginalUnchanged()
    {
        var original = WithHistory();
        var copy = original.Clone();

        copy.RecordAssignment(Str, GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, 2, 1);
        copy.AddNarrowing(new GDNarrowingConstraint(GDNarrowingKind.IsCheck, Str, At));

        Assert.AreEqual(1, original.AssignmentHistory.Count);
        Assert.AreEqual(1, original.ActiveNarrowings.Count);
        Assert.AreEqual(2, copy.AssignmentHistory.Count);
        Assert.AreEqual(2, copy.ActiveNarrowings.Count);
    }

    [TestMethod]
    public void Clone_MutatingTheOriginal_LeavesTheCopyUnchanged()
    {
        var original = WithHistory();
        var copy = original.Clone();

        original.RecordAssignment(Str, GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, 2, 1);
        original.ClearNarrowings();

        Assert.AreEqual(1, copy.AssignmentHistory.Count);
        Assert.AreEqual(1, copy.ActiveNarrowings.Count);
        Assert.AreEqual(2, original.AssignmentHistory.Count);
        Assert.AreEqual(0, original.ActiveNarrowings.Count);
    }

    [TestMethod]
    public void Clone_OfAClone_StaysIsolatedFromBoth()
    {
        var a = WithHistory();
        var b = a.Clone();
        var c = b.Clone();

        b.RecordAssignment(Str, GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, 2, 1);
        c.ClearNarrowings();

        Assert.AreEqual(1, a.AssignmentHistory.Count);
        Assert.AreEqual(1, a.ActiveNarrowings.Count);
        Assert.AreEqual(2, b.AssignmentHistory.Count);
        Assert.AreEqual(1, b.ActiveNarrowings.Count);
        Assert.AreEqual(1, c.AssignmentHistory.Count);
        Assert.AreEqual(0, c.ActiveNarrowings.Count);
    }

    [TestMethod]
    public void CloneUnion_CopiesTypesInOrderOriginsAndConfidence_AndIsIndependent()
    {
        var a = new GDTypeOrigin(GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, At);
        var duck = new GDTypeOrigin(GDTypeOriginKind.Assignment, GDTypeOriginConfidence.DuckTyped, At);
        var union = new GDUnionType();
        union.AddType(Str);                 // a type without origins, inserted first
        union.AddType(Int, a);
        union.AddType(Int, duck);
        union.AllHighConfidence = true;     // the clone re-derives it from the origins, as re-adding did

        var clone = union.CloneUnion();

        CollectionAssert.AreEqual(new[] { Int, Str }, clone.Types.ToArray(), "origin types first, then the rest");
        CollectionAssert.AreEqual(new[] { a, duck }, clone.GetOrigins(Int).ToArray());
        Assert.IsFalse(clone.AllHighConfidence);
        Assert.IsFalse(clone.IsFrozen);

        var b = new GDTypeOrigin(GDTypeOriginKind.Assignment, GDTypeOriginConfidence.Inferred, At);
        clone.AddType(Int, a);              // already tracked: kept once
        clone.ReplaceOrigin(Int, duck, b);
        clone.AddType(GDSemanticType.FromRuntimeTypeName("float"), b);

        CollectionAssert.AreEqual(new[] { a, b }, clone.GetOrigins(Int).ToArray());
        CollectionAssert.AreEqual(new[] { a, duck }, union.GetOrigins(Int).ToArray(), "the original is untouched");
        Assert.AreEqual(2, union.Types.Count);
    }

    [TestMethod]
    public void GodotGetMember_IsCachedAndResolvesInheritedMembers()
    {
        var provider = new GDGodotTypesProvider();

        var direct = provider.GetMember("Node", "get_parent");
        Assert.IsNotNull(direct);
        Assert.AreSame(direct, provider.GetMember("Node", "get_parent"));
        Assert.AreEqual("get_parent", direct.Name);

        var inherited = provider.GetMember("Node2D", "get_parent");
        Assert.IsNotNull(inherited, "members of base types resolve through the cache too");
        Assert.AreEqual(direct.Type, inherited.Type);

        Assert.IsNull(provider.GetMember("Node", "no_such_member_xyz"));
        Assert.IsNull(provider.GetMember("Node", "no_such_member_xyz"));
        Assert.IsNull(provider.GetMember("", "get_parent"));
    }
}
