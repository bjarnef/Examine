using System;
using System.Collections.Generic;
using System.Linq;
using Examine.Search;
using Lucene.Net.Documents;
using Lucene.Net.Facet;
using Lucene.Net.Facet.Range;
using Lucene.Net.Facet.SortedSet;
using Lucene.Net.Index;
using Lucene.Net.Queries.Function.ValueSources;
using Lucene.Net.Search;

namespace Examine.Lucene.Search
{

    /// <summary>
    /// An implementation of the search results returned from Lucene.Net
    /// </summary>
    public class LuceneSearchExecutor
    {
        private readonly QueryOptions _options;
        private readonly IEnumerable<SortField> _sortField;
        private readonly ISearchContext _searchContext;
        private readonly Query _luceneQuery;
        private readonly ISet<string> _fieldsToLoad;
        private readonly IEnumerable<IFacetField> _facetFields;
        private int? _maxDoc;

        internal LuceneSearchExecutor(QueryOptions options, Query query, IEnumerable<SortField> sortField, ISearchContext searchContext, ISet<string> fieldsToLoad, IEnumerable<IFacetField> facetFields)
        {
            _options = options ?? QueryOptions.Default;
            _luceneQuery = query ?? throw new ArgumentNullException(nameof(query));
            _fieldsToLoad = fieldsToLoad;
            _sortField = sortField ?? throw new ArgumentNullException(nameof(sortField));
            _searchContext = searchContext ?? throw new ArgumentNullException(nameof(searchContext));
            _facetFields = facetFields;
        }

        private int MaxDoc
        {
            get
            {
                if (_maxDoc == null)
                {
                    using (ISearcherReference searcher = _searchContext.GetSearcher())
                    {
                        _maxDoc = searcher.IndexSearcher.IndexReader.MaxDoc;
                    }
                }
                return _maxDoc.Value;
            }
        }

        public ISearchResults Execute()
        {
            var extractTermsSupported = CheckQueryForExtractTerms(_luceneQuery);

            if (extractTermsSupported)
            {
                //This try catch is because analyzers strip out stop words and sometimes leave the query
                //with null values. This simply tries to extract terms, if it fails with a null
                //reference then its an invalid null query, NotSupporteException occurs when the query is
                //valid but the type of query can't extract terms.
                //This IS a work-around, theoretically Lucene itself should check for null query parameters
                //before throwing exceptions.
                try
                {
                    var set = new HashSet<Term>();
                    _luceneQuery.ExtractTerms(set);
                }
                catch (NullReferenceException)
                {
                    //this means that an analyzer has stipped out stop words and now there are
                    //no words left to search on

                    //it could also mean that potentially a IIndexFieldValueType is throwing a null ref
                    return LuceneSearchResults.Empty;
                }
                catch (NotSupportedException)
                {
                    //swallow this exception, we should continue if this occurs.
                }
            }

            var maxResults = Math.Min((_options.Skip + 1) * _options.Take, MaxDoc);
            maxResults = maxResults >= 1 ? maxResults : QueryOptions.DefaultMaxResults;

            ICollector topDocsCollector;
            SortField[] sortFields = _sortField as SortField[] ?? _sortField.ToArray();
            if (sortFields.Length > 0)
            {
                topDocsCollector = TopFieldCollector.Create(
                    new Sort(sortFields), maxResults, false, false, false, false);
            }
            else
            {
                topDocsCollector = TopScoreDocCollector.Create(maxResults, true);
            }

            using (ISearcherReference searcher = _searchContext.GetSearcher())
            {
                FacetsCollector facetsCollector;
                if(_facetFields.Any())
                {
                    facetsCollector = new FacetsCollector();
                    searcher.IndexSearcher.Search(_luceneQuery, MultiCollector.Wrap(topDocsCollector, facetsCollector));
                }
                else
                {
                    facetsCollector = null;
                    searcher.IndexSearcher.Search(_luceneQuery, topDocsCollector);
                }

                TopDocs topDocs;
                if (sortFields.Length > 0)
                {
                    topDocs = ((TopFieldCollector)topDocsCollector).GetTopDocs(_options.Skip, _options.Take);
                }
                else
                {
                    topDocs = ((TopScoreDocCollector)topDocsCollector).GetTopDocs(_options.Skip, _options.Take);
                }

                var totalItemCount = topDocs.TotalHits;

                var results = new List<ISearchResult>();
                for (int i = 0; i < topDocs.ScoreDocs.Length; i++)
                {
                    var result = GetSearchResult(i, topDocs, searcher.IndexSearcher);
                    results.Add(result);
                }

                var facets = ExtractFacets(facetsCollector, searcher);

                return new LuceneSearchResults(results, totalItemCount, facets);
            }
        }

        private IReadOnlyDictionary<string, IFacetResult> ExtractFacets(FacetsCollector facetsCollector, ISearcherReference searcher)
        {
            var facets = new Dictionary<string, IFacetResult>(StringComparer.InvariantCultureIgnoreCase);
            if (facetsCollector == null || !_facetFields.Any())
            {
                return facets;
            }

            var facetFields = _facetFields.OrderBy(field => field.FacetField);

            SortedSetDocValuesReaderState sortedSetReaderState = null;

            foreach(var field in facetFields)
            {
                if (field is IFacetFullTextField facetFullTextField)
                {
                    ExtractFullTextFacets(facetsCollector, searcher, facets, sortedSetReaderState, field, facetFullTextField);
                }
                else if (field is IFacetLongField facetLongField)
                {
                    var longFacetCounts = new Int64RangeFacetCounts(facetLongField.Field, facetsCollector, facetLongField.LongRanges.AsLuceneRange().ToArray());

                    var longFacets = longFacetCounts.GetTopChildren(0, facetLongField.Field);

                    if(longFacets == null)
                    {
                        continue;
                    }

                    facets.Add(facetLongField.Field, new Examine.Search.FacetResult(longFacets.LabelValues.Select(labelValue => new FacetValue(labelValue.Label, labelValue.Value) as IFacetValue)));
                }
                else if (field is IFacetDoubleField facetDoubleField)
                {
                    var doubleFacetCounts = new DoubleRangeFacetCounts(facetDoubleField.Field, facetsCollector, facetDoubleField.DoubleRanges.AsLuceneRange().ToArray());
                    
                    var doubleFacets = doubleFacetCounts.GetTopChildren(0, facetDoubleField.Field);

                    if(doubleFacets == null)
                    {
                        continue;
                    }

                    facets.Add(facetDoubleField.Field, new Examine.Search.FacetResult(doubleFacets.LabelValues.Select(labelValue => new FacetValue(labelValue.Label, labelValue.Value) as IFacetValue)));
                }
                else if(field is IFacetFloatField facetFloatField)
                {
                    var floatFacetCounts = new DoubleRangeFacetCounts(facetFloatField.Field, new SingleFieldSource(facetFloatField.Field), facetsCollector, facetFloatField.FloatRanges.AsLuceneRange().ToArray());
                    
                    var floatFacets = floatFacetCounts.GetTopChildren(0, facetFloatField.Field);

                    if (floatFacets == null)
                    {
                        continue;
                    }

                    facets.Add(facetFloatField.Field, new Examine.Search.FacetResult(floatFacets.LabelValues.Select(labelValue => new FacetValue(labelValue.Label, labelValue.Value) as IFacetValue)));
                }
            }

            return facets;
        }

        private static void ExtractFullTextFacets(FacetsCollector facetsCollector, ISearcherReference searcher, Dictionary<string, IFacetResult> facets, SortedSetDocValuesReaderState sortedSetReaderState, IFacetField field, IFacetFullTextField facetFullTextField)
        {
            if (sortedSetReaderState == null || !sortedSetReaderState.Field.Equals(field.FacetField))
            {
                sortedSetReaderState = new DefaultSortedSetDocValuesReaderState(searcher.IndexSearcher.IndexReader, field.FacetField);
            }

            var sortedFacetsCounts = new SortedSetDocValuesFacetCounts(sortedSetReaderState, facetsCollector);

            if (facetFullTextField.Values != null && facetFullTextField.Values.Length > 0)
            {
                var facetValues = new List<FacetValue>();
                foreach (var label in facetFullTextField.Values)
                {
                    var value = sortedFacetsCounts.GetSpecificValue(facetFullTextField.Field, label);
                    facetValues.Add(new FacetValue(label, value));
                }
                facets.Add(facetFullTextField.Field, new Examine.Search.FacetResult(facetValues.OrderBy(value => value.Value).Take(facetFullTextField.MaxCount).OfType<IFacetValue>()));
            }
            else
            {
                var sortedFacets = sortedFacetsCounts.GetTopChildren(facetFullTextField.MaxCount, facetFullTextField.Field);

                if(sortedFacets == null)
                {
                    return;
                }

                facets.Add(facetFullTextField.Field, new Examine.Search.FacetResult(sortedFacets.LabelValues.Select(labelValue => new FacetValue(labelValue.Label, labelValue.Value) as IFacetValue)));
            }
        }

        private ISearchResult GetSearchResult(int index, TopDocs topDocs, IndexSearcher luceneSearcher)
        {
            // I have seen IndexOutOfRangeException here which is strange as this is only called in one place
            // and from that one place "i" is always less than the size of this collection. 
            // but we'll error check here anyways
            if (topDocs?.ScoreDocs.Length < index)
            {
                return null;
            }

            var scoreDoc = topDocs.ScoreDocs[index];

            var docId = scoreDoc.Doc;
            Document doc;
            if (_fieldsToLoad != null)
            {
                doc = luceneSearcher.Doc(docId, _fieldsToLoad);
            }
            else
            {
                doc = luceneSearcher.Doc(docId);
            }
            var score = scoreDoc.Score;
            var result = CreateSearchResult(doc, score);

            return result;
        }

        /// <summary>
        /// Creates the search result from a <see cref="Lucene.Net.Documents.Document"/>
        /// </summary>
        /// <param name="doc">The doc to convert.</param>
        /// <param name="score">The score.</param>
        /// <returns>A populated search result object</returns>
        private ISearchResult CreateSearchResult(Document doc, float score)
        {
            var id = doc.Get("id");

            if (string.IsNullOrEmpty(id) == true)
            {
                id = doc.Get(ExamineFieldNames.ItemIdFieldName);
            }

            var searchResult = new SearchResult(id, score, () =>
            {
                //we can use lucene to find out the fields which have been stored for this particular document
                var fields = doc.Fields;

                var resultVals = new Dictionary<string, List<string>>();

                foreach (var field in fields.Cast<Field>())
                {
                    var fieldName = field.Name;
                    var values = doc.GetValues(fieldName);

                    if (resultVals.TryGetValue(fieldName, out var resultFieldVals))
                    {
                        foreach (var value in values)
                        {
                            if (!resultFieldVals.Contains(value))
                            {
                                resultFieldVals.Add(value);
                            }
                        }
                    }
                    else
                    {
                        resultVals[fieldName] = values.ToList();
                    }
                }

                return resultVals;
            });

            return searchResult;
        }

        private bool CheckQueryForExtractTerms(Query query)
        {
            if (query is BooleanQuery bq)
            {
                foreach (BooleanClause clause in bq.Clauses)
                {
                    //recurse
                    var check = CheckQueryForExtractTerms(clause.Query);
                    if (!check)
                    {
                        return false;
                    }
                }
            }

            if (query is LateBoundQuery lbq)
            {
                return CheckQueryForExtractTerms(lbq.Wrapped);
            }

            Type queryType = query.GetType();

            if (typeof(TermRangeQuery).IsAssignableFrom(queryType)
                || typeof(WildcardQuery).IsAssignableFrom(queryType)
                || typeof(FuzzyQuery).IsAssignableFrom(queryType)
                || (queryType.IsGenericType && queryType.GetGenericTypeDefinition().IsAssignableFrom(typeof(NumericRangeQuery<>))))
            {
                return false; //ExtractTerms() not supported by TermRangeQuery, WildcardQuery,FuzzyQuery and will throw NotSupportedException 
            }

            return true;
        }
    }
}
