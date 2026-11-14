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

`AsAsyncEnumerable`, `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `FirstOrDefaultAsync`, `CountAsync` and `AnyAsync` run the query on
another thread and do not block the caller.

Datasets are local files for now.
