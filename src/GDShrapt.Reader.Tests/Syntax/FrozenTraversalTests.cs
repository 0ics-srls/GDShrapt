using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GDShrapt.Reader.Tests.Syntax
{
    /// <summary>
    /// LSAI#48: a frozen tree is walked over its token arrays and builds its reversed snapshot and position index
    /// lazily (linear scan for small forms). Every lookup and traversal must answer exactly like the mutable tree.
    /// </summary>
    [TestClass]
    public class FrozenTraversalTests
    {
        private const string Script = @"class_name Probe
extends Node

signal hit(amount)

var items = [1, 2, 3]
var table = {""a"": 1, ""b"": [4, 5]}

func run(x, y = 2):
    for i in range(x):
        if i % 2 == 0 and y > 1:
            items.append(i * y)
        elif i == 3:
            hit.emit(i)
        else:
            match i:
                1: pass
                _: return items.size()
    var f = func(a): return a + 1
    return f.call(x)
";

        private sealed class OrderVisitor : GDVisitor
        {
            public readonly List<string> Events = new List<string>();
            public override void WillVisit(GDNode node) => Events.Add("will:" + node.TypeName + "@" + node.StartLine + ":" + node.StartColumn);
            public override void DidLeft(GDNode node) => Events.Add("left:" + node.TypeName);
            public override void EnterListChild(GDNode node) => Events.Add("child:" + node.TypeName);
            public override void LeftListChild(GDNode node) => Events.Add("/child:" + node.TypeName);
        }

        private static List<string> Walk(GDNode tree, bool backward)
        {
            var visitor = new OrderVisitor();
            if (backward)
                tree.WalkInBackward(visitor);
            else
                tree.WalkIn(visitor);
            return visitor.Events;
        }

        [TestMethod]
        public void FrozenWalk_VisitsTheSameNodesInTheSameOrder_AsTheMutableTree()
        {
            var reader = new GDScriptReader();
            var mutable = reader.ParseFileContent(Script);
            var frozen = reader.ParseFileContent(Script);
            frozen.Freeze();

            CollectionAssert.AreEqual(Walk(mutable, backward: false), Walk(frozen, backward: false));
            CollectionAssert.AreEqual(Walk(mutable, backward: true), Walk(frozen, backward: true));
            Assert.IsTrue(Walk(frozen, backward: false).Count > 100, "the probe script must exercise a real tree");
        }

        private static string ArrayOf(int count)
        {
            var sb = new StringBuilder("var a = [");
            for (var i = 0; i < count; i++)
                sb.Append(i == 0 ? "" : ", ").Append("v").Append(i);
            return sb.Append("]\n").ToString();
        }

        private static GDTokensForm ValuesForm(GDClassDeclaration tree) =>
            tree.AllNodes.OfType<GDArrayInitializerExpression>().Single().Values.Form;

        [DataTestMethod]
        [DataRow(3)]    // linear-scan lookups
        [DataRow(40)]   // dictionary lookups
        public void FrozenForm_AnswersLookupsLikeTheMutableForm(int values)
        {
            var reader = new GDScriptReader();
            var code = ArrayOf(values);
            var mutable = ValuesForm(reader.ParseFileContent(code));
            var frozenTree = reader.ParseFileContent(code);
            frozenTree.Freeze();
            var frozen = ValuesForm(frozenTree);

            var mutableTokens = mutable.Direct().ToList();
            var frozenTokens = frozen.Direct().ToList();
            Assert.AreEqual(mutableTokens.Count, frozenTokens.Count);
            Assert.IsTrue(frozenTokens.Count > values, "values plus separators");

            CollectionAssert.AreEqual(
                mutable.Reversed().Select(t => t.ToString()).ToList(),
                frozen.Reversed().Select(t => t.ToString()).ToList());
            Assert.AreEqual(mutable.LastToken?.ToString(), frozen.LastToken?.ToString());

            for (var i = 0; i < frozenTokens.Count; i++)
            {
                Assert.IsTrue(frozen.Contains(frozenTokens[i]), $"token {i}");
                CollectionAssert.AreEqual(
                    mutable.GetAllTokensAfter(mutableTokens[i]).Select(t => t.ToString()).ToList(),
                    frozen.GetAllTokensAfter(frozenTokens[i]).Select(t => t.ToString()).ToList(),
                    $"tokens after {i}");
            }

            // A token of another tree is not part of the frozen form.
            Assert.IsFalse(frozen.Contains(mutableTokens.OfType<GDNode>().First()));
        }

        [TestMethod]
        public void FrozenTypedList_IndexOf_CountsOnlyItsItems()
        {
            var tree = new GDScriptReader().ParseFileContent(ArrayOf(30));
            tree.Freeze();
            var values = tree.AllNodes.OfType<GDArrayInitializerExpression>().Single().Values;

            for (var i = 0; i < values.Count; i++)
                Assert.AreEqual(i, values.IndexOf(values[i]));
        }
    }
}
