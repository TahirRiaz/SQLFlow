namespace SQLFlowCore.Lineage
{
    internal class LineageEdge
    {
        internal int? FromObjectMK { get; set; }
        internal int? ToObjectMK { get; set; }
        internal string FromObject { get; set; }
        internal string ToObject { get; set; }
        internal int Step { get; set; }
        internal bool Circular { get; set; }
    }
}
