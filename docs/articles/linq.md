# LINQ over files

`ParquetNeo.Linq` queries a file, or all the files of a directory (and the folders below it), with LINQ. It is not trim or Native
AOT safe: it reads expression trees and builds types at run time.

```csharp
using ParquetNeo.Linq;

var people = ParquetDataset.Open<Person>("people/");
var names = people.Where(p => p.Born >= new DateOnly(1900, 1, 1) && p.Name != null)
                  .OrderBy(p => p.Born)
                  .Select(p => new { p.Name, p.Born })
                  .Take(100)
                  .ToList();
```

`ParquetDataset<T>` is an `IQueryable<T>`. Every operator of LINQ to objects works on the rows that are read, and what is added is
what lets it read less:

- **Files, row groups and pages are skipped.** The `Where`s at the start of the query, or after an ordering (which is the same to
  the result), are turned into a `ParquetFilter` (see
  [Skipping what is not needed](filtering.md)): comparisons with constants and captured values, null tests, boolean properties,
  `Contains` on a list, nested properties, `&&`, `||` and `!`. The whole predicate is still applied to the rows that are read, so
  the result is exact.
- **Only the columns a query uses are read**, when it ends in a `Select` after only `Where`s, ordering and paging.
- **`Count()`, `LongCount()` and `Any()` are answered from the footers** of the files when they can be.
- **Files are read in parallel**, so without an `OrderBy` the rows come in no particular order.

```csharp
var plan = people.Where(p => p.Id > 5000).Explain();   // which files and row groups are skipped, which columns are read
int count = people.Count();                            // from the footers: no data is read
await foreach (var row in people.Where(p => p.Id > 5000).AsAsyncEnumerable()) { }
```

`people.ReadStatistics` says how many files were opened, how many requests were made and how many bytes were read by the queries of a
dataset (see [Reading from S3 and Azure](object-storage.md#how-much-was-read)).

### Asynchronous queries

`AsAsyncEnumerable`, `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync` and `ExplainAsync` run
the query without a thread waiting for the files: the files are listed, opened and read with asynchronous calls, by up to
`MaxDegreeOfParallelism` readers at a time, and the rows they decode go through a channel of a bounded size, so a slow consumer holds
the readers back, and stopping early (`Take`, `FirstAsync`, `break`, a cancelled token) stops them.

The operators that work on a row as it arrives (`Where`, `Select`, `Skip`, `Take`, and `First`, `FirstOrDefault`, `Any` and `Count` to end
with) run as asynchronous LINQ over the rows as they come. The first operator that needs every row (`OrderBy`, `GroupBy`, `Join`,
`Distinct`, `Reverse`, an aggregate, ...) is given the rows after they have been awaited into a list, which is what it holds in LINQ to
objects anyway, so a query does not hold more rows than when it is run with `foreach`; that operator and the ones after it run as LINQ to
objects does, over memory (on a pool thread, since that is work and not waiting). The result of an asynchronous query is the one of the
synchronous query, and the tests check that for the operators above. Plain `foreach` over the query is synchronous, as `IEnumerable` is.

Datasets are local files, or the files of an object storage: see [Reading from S3 and Azure](object-storage.md).
