using ZeroCdp.Dom;
using ZeroCdp.Selectors;
using Xunit;

namespace ZeroCdp.Tests;

public class SelectorTests
{
    // A hand-written CDP Node payload, including a closed shadow root, so the parser and matcher
    // can be exercised without a browser.
    private const string DocumentJson = """
    {"nodeId":1,"nodeType":9,"nodeName":"#document","children":[
      {"nodeId":2,"nodeType":1,"nodeName":"DIV","localName":"div","attributes":["id","host","class","box wide"],
       "children":[
         {"nodeId":3,"nodeType":1,"nodeName":"P","localName":"p","attributes":["class","item"],
          "children":[{"nodeId":4,"nodeType":3,"nodeName":"#text","nodeValue":"hello world"}]},
         {"nodeId":5,"nodeType":1,"nodeName":"SPAN","localName":"span","attributes":["data-x","1","title","abc-def"]},
         {"nodeId":6,"nodeType":1,"nodeName":"INPUT","localName":"input","attributes":["type","checkbox","checked",""]}
       ],
       "shadowRoots":[
         {"nodeId":7,"nodeType":11,"nodeName":"#document-fragment","children":[
           {"nodeId":8,"nodeType":1,"nodeName":"BUTTON","localName":"button","attributes":["class","inner"]}
         ]}
       ]}
    ]}
    """;

    [Theory]
    [InlineData("div", 1)]
    [InlineData("*", 5)]
    [InlineData("#host", 1)]
    [InlineData(".box", 1)]
    [InlineData(".wide.box", 1)]
    [InlineData("div p", 1)]
    [InlineData("div > p", 1)]
    [InlineData("p + span", 1)]
    [InlineData("p ~ input", 1)]
    [InlineData("div > span", 1)]
    [InlineData("[data-x]", 1)]
    [InlineData("[data-x=\"1\"]", 1)]
    [InlineData("[title^=abc]", 1)]
    [InlineData("[title$=def]", 1)]
    [InlineData("[title*=c-d]", 1)]
    [InlineData("[title|=abc]", 1)]
    [InlineData("p:first-child", 1)]
    [InlineData("input:last-child", 1)]
    [InlineData("span:nth-child(2)", 1)]
    [InlineData("span:nth-child(odd)", 0)]
    [InlineData("span:nth-child(2n)", 1)]
    [InlineData("p:only-child", 0)]
    [InlineData(":checked", 1)]
    [InlineData("div:not(.nope)", 1)]
    [InlineData("p:not(.item)", 0)]
    [InlineData("button.inner", 1)]          // reaches into the shadow root
    [InlineData("div button", 1)]            // descendant across the shadow boundary
    [InlineData("nope", 0)]
    public void Matches_the_expected_number_of_elements(string selector, int expected)
    {
        DomNode root = DomTreeParser.Parse(System.Text.Encoding.UTF8.GetBytes(DocumentJson))!;
        CompiledSelector[] compiled = CssSelectorParser.ParseList(selector);

        int count = 0;
        Stack<DomNode> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DomNode node = pending.Pop();
            if (node.IsElement && SelectorMatcher.MatchesAny(compiled, node))
            {
                count++;
            }

            foreach (DomNode child in node.TraversalChildren())
            {
                pending.Push(child);
            }
        }

        Assert.Equal(expected, count);
    }

    [Fact]
    public void Reads_text_content_of_a_subtree()
    {
        DomNode root = DomTreeParser.Parse(System.Text.Encoding.UTF8.GetBytes(DocumentJson))!;
        DomNode paragraph = Find(root, "p")!;

        Assert.Equal("hello world", paragraph.TextContent());
    }

    [Fact]
    public void Parses_a_selector_list()
    {
        CompiledSelector[] compiled = CssSelectorParser.ParseList("h1, h2 > span, #x .y");
        Assert.Equal(3, compiled.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("div >")]
    [InlineData("div::before")]
    [InlineData("div:hover")]
    [InlineData("[unclosed")]
    public void Rejects_selectors_outside_the_supported_subset(string selector)
    {
        Assert.Throws<SelectorSyntaxException>(() => CssSelectorParser.ParseList(selector));
    }

    private static DomNode? Find(DomNode root, string localName)
    {
        Stack<DomNode> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DomNode node = pending.Pop();
            if (node.IsElement && node.LocalName == localName)
            {
                return node;
            }

            foreach (DomNode child in node.TraversalChildren())
            {
                pending.Push(child);
            }
        }

        return null;
    }
}
