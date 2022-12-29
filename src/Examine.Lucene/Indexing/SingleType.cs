using Examine.Lucene.Providers;
using Lucene.Net.Documents;
using Lucene.Net.Facet;
using Lucene.Net.Facet.SortedSet;
using Lucene.Net.Search;
using Microsoft.Extensions.Logging;

namespace Examine.Lucene.Indexing
{
    public class SingleType : IndexFieldRangeValueType<float>
    {
        private readonly bool _isFacetable;
        private readonly bool _taxonomyIndex;

        public SingleType(string fieldName, ILoggerFactory logger, bool store = true, bool isFacetable = false, bool taxonomyIndex = false)
            : base(fieldName, logger, store)
        {
            _isFacetable = isFacetable;
            _taxonomyIndex = taxonomyIndex;
        }

        /// <summary>
        /// Can be sorted by the normal field name
        /// </summary>
        public override string SortableFieldName => FieldName;

        protected override void AddSingleValue(Document doc, object value)
        {
            if (!TryConvert(value, out float parsedVal))
                return;

            doc.Add(new DoubleField(FieldName,parsedVal, Store ? Field.Store.YES : Field.Store.NO));

            if (_isFacetable && _taxonomyIndex)
            {
                doc.Add(new FacetField(FieldName, parsedVal.ToString()));
                doc.Add(new SingleDocValuesField(FieldName, parsedVal));
            }
            else if (_isFacetable && !_taxonomyIndex)
            {
                doc.Add(new SortedSetDocValuesFacetField(FieldName, parsedVal.ToString()));
                doc.Add(new SingleDocValuesField(FieldName, parsedVal));
            }
        }

        public override Query GetQuery(string query)
        {
            return !TryConvert(query, out float parsedVal) ? null : GetQuery(parsedVal, parsedVal);
        }

        public override Query GetQuery(float? lower, float? upper, bool lowerInclusive = true, bool upperInclusive = true)
        {
            return NumericRangeQuery.NewDoubleRange(FieldName,
                lower ?? float.MinValue,
                upper ?? float.MaxValue, lowerInclusive, upperInclusive);
        }
    }
}
