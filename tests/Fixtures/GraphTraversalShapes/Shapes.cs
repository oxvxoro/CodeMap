namespace Fixture;

// Diamond: A -> B, A -> C, B -> D, C -> D. D is reachable from A via two distinct depth-2
// paths (through B and through C), so a recursive traversal that keeps per-path via-edge
// evidence can produce two rows for D at depth 2 unless collapsed to one.
public class Diamond
{
    public void A() { B(); C(); }
    public void B() { D(); }
    public void C() { D(); }
    public void D() { }
}

// Cycle: A -> B -> C -> A. Traversal must terminate at MaxDepth without an unbounded loop.
public class Cycle
{
    public void A() { B(); }
    public void B() { C(); }
    public void C() { A(); }
}

// Two distinct call occurrences on the same source line, calling the same target: exercises
// via-edge selection when candidate edges share source/target/kind but differ only by column.
public class DuplicateCallsSameLine
{
    private readonly Diamond diamond = new();
    public void CallTwice() { diamond.A(); diamond.A(); }
}
