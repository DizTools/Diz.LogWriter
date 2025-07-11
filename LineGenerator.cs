﻿using System;
using System.Collections.Generic;
using System.Linq;
using IX.Library.Collections;

namespace Diz.LogWriter;

public class LineGenerator
{
    public ILogCreatorForGenerator LogCreator { get; }
    public Dictionary<string, AssemblyPartialLineGenerator> Generators { get; }
    public LogCreatorLineFormatter LogCreatorLineFormatter { get; }

    public LineGenerator(ILogCreatorForGenerator logCreator, string formatStr)
    {
        LogCreator = logCreator;
        Generators = CreateAssemblyGenerators();
        LogCreatorLineFormatter = new LogCreatorLineFormatter(formatStr, Generators);
    }
        
    public string GenerateSpecialLine(string type, int offset = -1) => GenerateLine(offset, LogCreator.Settings.GenerateFullLine, type);
    public string GenerateNormalLine(int offset) => GenerateLine(offset);

    private string GenerateLine(int offset, bool generateFullLine = true, string overrideFormatterName = null)
    {
        var line = "";
        foreach (var columnFormat in LogCreatorLineFormatter.ColumnFormats)
        {
            var column = GenerateColumn(offset, columnFormat, overrideFormatterName);
            if (generateFullLine || (!generateFullLine && (columnFormat.Value == "label" || columnFormat.Value == "code" || string.IsNullOrWhiteSpace(column))))
                line += column;
        }

        line = line.TrimEnd();
        return string.IsNullOrEmpty(line) ? " " : line;
    }

    private string GenerateColumn(int offset, LogCreatorLineFormatter.ColumnFormat columnFormat, string overrideFormatterName = null)
    {
        if (columnFormat.IsLiteral)
            return columnFormat.Value;

        var formatter = SelectFinalColumnFormatter(columnFormat, overrideFormatterName);
        return GenerateColumnFromFormatter(offset, formatter);
    }

    private string GenerateColumnFromFormatter(int offset, LogCreatorLineFormatter.ColumnFormat columnFormat)
    {
        var columnGenerator = GetGeneratorFor(columnFormat.Value);
        return columnGenerator.Emit(columnFormat.SanitizeOffset(offset), columnFormat.LengthOverride);
    }

    private LogCreatorLineFormatter.ColumnFormat SelectFinalColumnFormatter(LogCreatorLineFormatter.ColumnFormat columnFormat, string overrideName = null)
    {
        return overrideName != null 
            ? BuildSpecialFormatterFrom(columnFormat, overrideName) 
            : columnFormat;
    }

    private LogCreatorLineFormatter.ColumnFormat BuildSpecialFormatterFrom(LogCreatorLineFormatter.ColumnFormat originalColumn, string specialModifierStr)
    {
        var ignoreOffset = false;
        string val;
        if (originalColumn.Value != "code")
        {
            ignoreOffset = true;
            val = "%empty";
        }
        else
        {
            val = $"%{specialModifierStr}";
        }

        return new LogCreatorLineFormatter.ColumnFormat
        {
            LengthOverride = originalColumn.LengthOverride ?? GetGeneratorFor(originalColumn.Value).DefaultLength,
            IgnoreOffset = ignoreOffset,
            Value = val,
        };
    }

    public AssemblyPartialLineGenerator GetGeneratorFor(string parameter)
    {
        if (!Generators.TryGetValue(parameter, out var generator))
            throw new InvalidOperationException($"Can't find generator for {parameter}");
        
        return generator;
    }
        
    public Dictionary<string, AssemblyPartialLineGenerator> CreateAssemblyGenerators()
    {
        var generators = AssemblyGeneratorRegistration.Create();
        generators.ForEach(kvp => kvp.Value.LogCreator = LogCreator);
        return generators;
    }
}