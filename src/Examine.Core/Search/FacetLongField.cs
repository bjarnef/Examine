namespace Examine.Search
{
    public class FacetLongField : IFacetLongField
    {
        public string Field { get; }

        public Int64Range[] LongRanges { get; }

        public string FacetField { get; set; }

        public FacetLongField(string field, Int64Range[] longRanges)
        {
            Field = field;
            LongRanges = longRanges;
            FacetField = ExamineFieldNames.DefaultFacetsName;
        }
    }
}
