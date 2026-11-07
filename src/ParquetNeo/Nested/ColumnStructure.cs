using ParquetNeo.Schema;

namespace ParquetNeo.Nested;

/// <summary>
/// The shape of the data in one column, as validity and offsets instead of definition and repetition levels: for each
/// optional node on the way to the column whether its value is present, and for each repeated node where each list
/// starts and ends. It is the columnar form of nested data (the same idea as the validity and offsets of an Arrow
/// array), and it converts both ways with the levels the reader returns and the writer takes.
/// </summary>
/// <remarks>
/// <para>
/// Only the nodes that can be missing or repeated take part; required nodes add nothing. The <see cref="Levels"/> come
/// outermost first. Every level has one entry per <i>item</i> of its depth: depth 0 has one item per row, and the items
/// of depth <c>d</c> are the elements of the <c>d</c>-th repeated level. Where an ancestor is missing, the entries of the
/// optional levels below it (at the same depth) say <c>false</c> and a repeated level below it says an empty list.
/// </para>
/// <para>
/// The values of the column are not part of the structure: they are the values of the entries whose every ancestor is
/// present, in order, which is exactly what <see cref="Core.ColumnWriter"/> takes and <see cref="Core.ColumnReader"/> returns.
/// </para>
/// </remarks>
public sealed class ColumnStructure
{
    private readonly StructurePlan _plan;

    private ColumnStructure(PrimitiveNode column, StructurePlan plan, StructureLevel[] levels)
    {
        Column = column;
        _plan = plan;
        Levels = levels;
        RowCount = levels[0].ItemCount;
    }

    public PrimitiveNode Column { get; }

    /// <summary>The optional and repeated nodes on the way to the column, outermost first.</summary>
    public IReadOnlyList<StructureLevel> Levels { get; }

    public int RowCount { get; }

    /// <summary>Builds a structure from its levels, checking that they fit the column and each other.</summary>
    /// <exception cref="ArgumentException">The levels do not match the optional and repeated nodes of the column, or disagree on how many items there are.</exception>
    public static ColumnStructure Create(PrimitiveNode column, IReadOnlyList<StructureLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(levels);
        var plan = StructurePlan.For(column);
        if (plan.Count == 0)
        {
            throw new ArgumentException($"Column '{column.Name}' is required all the way up, so it has no structure.", nameof(column));
        }

        if (levels.Count != plan.Count)
        {
            throw new ArgumentException($"Column '{string.Join('.', column.Path)}' has {plan.Count} optional or repeated nodes but {levels.Count} levels were given.", nameof(levels));
        }

        var copy = new StructureLevel[levels.Count];
        for (int j = 0; j < copy.Length; j++)
        {
            copy[j] = levels[j] ?? throw new ArgumentException("A level is null.", nameof(levels));
            if (!ReferenceEquals(copy[j].Node, plan.Nodes[j]))
            {
                throw new ArgumentException($"Level {j} is for node '{copy[j].Node.Name}' but the column's node there is '{plan.Nodes[j].Name}'.", nameof(levels));
            }
        }

        int rows = copy[0].ItemCount;
        for (int j = 0; j < copy.Length; j++)
        {
            int depth = plan.DepthBefore[j];
            int expected = depth == 0 ? rows : copy[plan.RepeatedLevels[depth - 1]].ElementCount;
            if (copy[j].ItemCount != expected)
            {
                throw new ArgumentException($"Level {j} ('{copy[j].Node.Name}') has {copy[j].ItemCount} items but the structure above it has {expected}.", nameof(levels));
            }
        }

        return new ColumnStructure(column, plan, copy);
    }

    /// <summary>
    /// Builds the structure from the definition and repetition levels of whole rows, as <see cref="Core.ColumnReader"/>
    /// returns them.
    /// </summary>
    /// <param name="repetitionLevels">Empty when the column is not repeated.</param>
    /// <exception cref="ArgumentException">The levels are out of range or inconsistent, or the column has no optional or repeated node.</exception>
    public static ColumnStructure FromLevels(PrimitiveNode column, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        var plan = StructurePlan.For(column);
        if (plan.Count == 0)
        {
            throw new ArgumentException($"Column '{column.Name}' is required all the way up, so it has no structure.", nameof(column));
        }

        int entries = definitionLevels.Length;
        bool repeated = column.MaxRepetitionLevel > 0;
        if (repeated ? repetitionLevels.Length != entries : !repetitionLevels.IsEmpty)
        {
            throw new ArgumentException(repeated
                ? "A repeated column needs one repetition level per definition level."
                : "A column that is not repeated has no repetition levels.", nameof(repetitionLevels));
        }

        if (definitionLevels.IndexOfAnyExceptInRange(0, column.MaxDefinitionLevel) >= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definitionLevels), $"Definition levels must be between 0 and {column.MaxDefinitionLevel}.");
        }

        if (repeated && entries > 0)
        {
            if (repetitionLevels.IndexOfAnyExceptInRange(0, column.MaxRepetitionLevel) >= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(repetitionLevels), $"Repetition levels must be between 0 and {column.MaxRepetitionLevel}.");
            }

            if (repetitionLevels[0] != 0)
            {
                throw new ArgumentException("The first entry must start a row, so its repetition level must be 0.", nameof(repetitionLevels));
            }
        }

        var validity = new List<bool>?[plan.Count];
        var counts = new List<int>?[plan.Count];
        for (int j = 0; j < plan.Count; j++)
        {
            if (plan.IsRepeated(j))
            {
                counts[j] = new List<int>();
            }
            else
            {
                validity[j] = new List<bool>();
            }
        }

        for (int entry = 0; entry < entries; entry++)
        {
            int definition = definitionLevels[entry];
            int repetition = repeated ? repetitionLevels[entry] : 0;
            for (int j = 0; j < plan.Count; j++)
            {
                int depth = plan.DepthBefore[j];
                if (repetition > depth || (depth > 0 && definition < plan.DefinitionLevelOfRepeated(depth)))
                {
                    continue; // the entry carries on an item already counted, or it has no item at this depth
                }

                if (plan.IsRepeated(j))
                {
                    counts[j]!.Add(0);
                }
                else
                {
                    validity[j]!.Add(definition >= j + 1);
                }
            }

            foreach (int j in plan.RepeatedLevels)
            {
                // The entry holds an element of this list when it reaches the list's node, and starts a new one when it
                // does not repeat at a deeper level.
                if (definition >= j + 1 && repetition <= plan.DepthBefore[j] + 1)
                {
                    if (counts[j]!.Count == 0)
                    {
                        throw new ArgumentException($"Entry {entry} continues a list that no earlier entry started.", nameof(repetitionLevels));
                    }

                    counts[j]![^1]++;
                }
            }
        }

        var levels = new StructureLevel[plan.Count];
        for (int j = 0; j < plan.Count; j++)
        {
            if (plan.IsRepeated(j))
            {
                var offsets = new int[counts[j]!.Count + 1];
                for (int item = 0; item < counts[j]!.Count; item++)
                {
                    offsets[item + 1] = checked(offsets[item] + counts[j]![item]);
                }

                levels[j] = StructureLevel.ForRepeated(plan.Nodes[j], offsets);
            }
            else
            {
                levels[j] = StructureLevel.ForOptional(plan.Nodes[j], validity[j]!.ToArray());
            }
        }

        return new ColumnStructure(column, plan, levels);
    }

    /// <summary>Returns the number of leading entries of a batch of repetition levels that belong to rows known to be complete.</summary>
    /// <remarks>
    /// A batch of a repeated column can end in the middle of a row. The entries from the last one with repetition level 0
    /// on may be the start of a row that goes on in the next batch, so assemble only the entries before them and carry the
    /// rest over. At the end of the column the whole batch is complete.
    /// </remarks>
    public static int CountEntriesInCompleteRows(ReadOnlySpan<int> repetitionLevels)
    {
        int lastRowStart = repetitionLevels.LastIndexOf(0);
        return lastRowStart < 0 ? 0 : lastRowStart;
    }

    /// <summary>The definition and repetition levels of the structure, ready for <see cref="Core.ColumnWriter"/>.</summary>
    /// <param name="repetitionLevels">Empty when the column is not repeated.</param>
    public void GetLevels(out int[] definitionLevels, out int[] repetitionLevels)
    {
        var definition = new List<int>();
        var repetition = new List<int>();
        var positions = new int[_plan.Count];
        for (int row = 0; row < RowCount; row++)
        {
            Emit(0, 0, 0, positions, definition, repetition);
        }

        for (int level = 0; level < positions.Length; level++)
        {
            if (positions[level] != Levels[level].ItemCount)
            {
                throw new InvalidOperationException($"The structure is inconsistent: level {level} ('{Levels[level].Node.Name}') was expected to be used by {positions[level]} items but has {Levels[level].ItemCount}; entries below a missing value must say so.");
            }
        }

        definitionLevels = definition.ToArray();
        repetitionLevels = Column.MaxRepetitionLevel > 0 ? repetition.ToArray() : [];
    }

    private void Emit(int level, int repetition, int definition, int[] positions, List<int> definitionLevels, List<int> repetitionLevels)
    {
        if (level == _plan.Count)
        {
            definitionLevels.Add(definition);
            repetitionLevels.Add(repetition);
            return;
        }

        var current = Levels[level];
        if (current.Kind == StructureLevelKind.Optional)
        {
            if (!current.Validity![positions[level]++])
            {
                SkipBelow(level + 1, positions);
                definitionLevels.Add(definition);
                repetitionLevels.Add(repetition);
                return;
            }

            Emit(level + 1, repetition, definition + 1, positions, definitionLevels, repetitionLevels);
            return;
        }

        int item = positions[level]++;
        int count = current.Offsets![item + 1] - current.Offsets[item];
        if (count == 0)
        {
            definitionLevels.Add(definition);
            repetitionLevels.Add(repetition);
            return;
        }

        int repeatsAt = _plan.DepthBefore[level] + 1;
        for (int element = 0; element < count; element++)
        {
            Emit(level + 1, element == 0 ? repetition : repeatsAt, definition + 1, positions, definitionLevels, repetitionLevels);
        }
    }

    /// <summary>
    /// Steps over the entries of the levels below a missing value: the optional ones at the same depth, and the first
    /// repeated one (an empty list), whose elements, and everything inside them, do not exist.
    /// </summary>
    private void SkipBelow(int level, int[] positions)
    {
        for (; level < _plan.Count; level++)
        {
            positions[level]++;
            if (_plan.IsRepeated(level))
            {
                return;
            }
        }
    }
}
