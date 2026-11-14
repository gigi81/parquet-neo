using System.Linq.Expressions;

namespace ParquetNeo.Linq.Tests;

/// <summary>Random predicates over the dataset give the rows that the same predicates give over the rows in memory, whatever the statistics let it skip.</summary>
public class RandomQueryTests
{
    private static readonly string[] Cities = ["Rome", "Paris", "Oslo", "Lima", "Cairo", "Nowhere"];

    private static Expression Leaf(ParameterExpression row, Random random)
    {
        Expression Property(string name) => Expression.Property(row, name);
        Expression Constant<T>(T value) => Expression.Constant(value, typeof(T));
        var comparisons = new Func<Expression, Expression, Expression>[]
        {
            Expression.Equal, Expression.NotEqual, Expression.LessThan, Expression.LessThanOrEqual, Expression.GreaterThan, Expression.GreaterThanOrEqual,
        };
        var compare = comparisons[random.Next(comparisons.Length)];
        switch (random.Next(10))
        {
            case 0: return compare(Property("Id"), Constant(random.Next(-100, 3600)));
            case 1: return compare(Property("Age"), Constant(random.Next(10, 85)));
            case 2: return random.Next(2) == 0 ? Expression.Equal(Property("City"), Constant(Cities[random.Next(Cities.Length)])) : Expression.NotEqual(Property("City"), Constant(Cities[random.Next(Cities.Length)]));
            case 3: return random.Next(3) == 0 ? Expression.Equal(Property("Name"), Constant<string?>(null)) : Expression.NotEqual(Property("Name"), Constant<string?>($"name-{random.Next(0, 60)}"));
            case 4: return random.Next(3) == 0 ? Expression.Equal(Property("Score"), Constant<double?>(null)) : compare(Property("Score"), Constant<double?>(random.Next(0, 20) * 1.5));
            case 5: return random.Next(2) == 0 ? Property("Active") : Expression.Not(Property("Active"));
            case 6:
                var level = Constant(random.Next(2) == 0 ? Level.Junior : Level.Senior);
                return random.Next(2) == 0 ? Expression.Equal(Property("Level"), level) : Expression.NotEqual(Property("Level"), level);
            case 7: return compare(Property("Born"), Constant(new DateOnly(1950, 1, 1).AddDays(random.Next(0, 10_000))));
            case 8: return compare(Property("Zip"), Constant(10_000 + random.Next(0, 950)));
            default: return compare(Expression.Property(Property("Home"), "Number"), Constant(random.Next(0, 100)));
        }
    }

    private static Expression Predicate(ParameterExpression row, Random random, int depth)
    {
        if (depth == 0 || random.Next(3) == 0)
        {
            return Leaf(row, random);
        }

        return random.Next(3) switch
        {
            0 => Expression.AndAlso(Predicate(row, random, depth - 1), Predicate(row, random, depth - 1)),
            1 => Expression.OrElse(Predicate(row, random, depth - 1), Predicate(row, random, depth - 1)),
            _ => Expression.Not(Predicate(row, random, depth - 1)),
        };
    }

    [Test]
    public async Task APredicate_GivesTheRowsItGivesInMemory()
    {
        using var data = new TestDataset();
        var dataset = data.Open();
        var memory = data.All.AsQueryable();
        var random = new Random(2024);
        var failures = new List<string>();
        int skippingQueries = 0;
        for (int n = 0; n < 150; n++)
        {
            var row = Expression.Parameter(typeof(Person), "p");
            var lambda = Expression.Lambda<Func<Person, bool>>(Predicate(row, random, depth: 3), row);

            var expected = memory.Where(lambda).Select(p => p.Id).Order().ToList();
            var query = dataset.Where(lambda);
            var actual = query.Select(p => p.Id).ToList();
            actual.Sort();
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{lambda}: {actual.Count} rows, expected {expected.Count}");
            }

            var plan = query.Explain();
            skippingQueries += plan.FilesSkipped + plan.RowGroupsSkipped > 0 ? 1 : 0;
        }

        await Assert.That(failures.Take(5).ToArray()).IsEmpty();

        // Not empty of meaning: the statistics ruled something out for a fair share of the queries.
        await Assert.That(skippingQueries).IsGreaterThan(30);
    }

    [Test]
    public async Task APredicateAfterAnOrdering_GivesTheRowsItGivesInMemory_AndSkipsAsMuch()
    {
        using var data = new TestDataset();
        var dataset = data.Open();
        var memory = data.All.AsQueryable();
        var random = new Random(77);
        var failures = new List<string>();
        int worse = 0;
        int skippingQueries = 0;
        for (int n = 0; n < 150; n++)
        {
            var row = Expression.Parameter(typeof(Person), "p");
            var lambda = Expression.Lambda<Func<Person, bool>>(Predicate(row, random, depth: 3), row);

            var expected = memory.OrderBy(p => p.Age).ThenByDescending(p => p.Id).Where(lambda).Select(p => p.Id).ToList();
            var query = dataset.OrderBy(p => p.Age).ThenByDescending(p => p.Id).Where(lambda);
            var actual = query.Select(p => p.Id).ToList();
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{lambda}: {actual.Count} rows, expected {expected.Count}");
            }

            var plan = query.Explain();
            var first = dataset.Where(lambda).Explain();
            skippingQueries += plan.FilesSkipped + plan.RowGroupsSkipped > 0 ? 1 : 0;
            worse += plan.FilesSkipped < first.FilesSkipped || plan.RowGroupsSkipped < first.RowGroupsSkipped ? 1 : 0;
        }

        await Assert.That(failures.Take(5).ToArray()).IsEmpty();
        await Assert.That(skippingQueries).IsGreaterThan(30);

        // The ordering in front of the condition does not make the query skip less than the condition alone.
        await Assert.That(worse).IsEqualTo(0);
    }
}
