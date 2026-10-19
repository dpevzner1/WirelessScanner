using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace WirelessScanner.Infrastructure;

/// <summary>
/// Brings an existing SQLite database up to the current EF model (DR-0003).
/// EnsureCreated() does nothing when the file already exists, so databases created by older
/// versions would never receive new tables (e.g. the saturation test tables). This upgrader
/// takes EF's own generated DDL, and executes CREATE TABLE / CREATE INDEX statements for objects
/// that do not exist yet. It never drops or alters existing data. Schema version is recorded in
/// PRAGMA user_version.
/// </summary>
public static class SchemaMigrator
{
    public const int CurrentSchemaVersion = 4; // 1 = sessions/samples/settings, 2 = + saturation test tables, 3 = + diagnostic columns (DR-0012), 4 = + connection test tables (DR-0014)

    private static readonly Regex CreateObject = new(
        @"^\s*CREATE\s+(?<unique>UNIQUE\s+)?(?<kind>TABLE|INDEX)\s+""(?<name>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static MigrationResult Upgrade(DbContext context)
    {
        context.Database.EnsureCreated();
        var created = new List<string>();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = context.Database.GetDbConnection();
        bool opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            opened = true;
        }

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index')";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            int before = ReadUserVersion(connection);
            string script = context.Database.GenerateCreateScript();
            foreach (var statement in script.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                var m = CreateObject.Match(statement);
                if (!m.Success) continue;
                string name = m.Groups["name"].Value;
                if (existing.Contains(name)) continue;

                string ddl = statement.Insert(m.Groups["name"].Index - 1, "IF NOT EXISTS ");
                using var cmd = connection.CreateCommand();
                cmd.CommandText = ddl;
                cmd.ExecuteNonQuery();
                created.Add(name);
                existing.Add(name);
            }

            // Columns added to existing tables (e.g. schema 3): ALTER TABLE ... ADD COLUMN for nullable model columns that
            // the table lacks. Never drops, renames or rewrites existing data; non-nullable additions are refused.
            foreach (var entity in context.Model.GetEntityTypes())
            {
                string? table = entity.GetTableName();
                if (table == null) continue;
                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) have.Add(reader.GetString(1));
                }
                if (have.Count == 0) continue; // table was just created with every column
                var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entity.GetSchema());
                foreach (var prop in entity.GetProperties())
                {
                    string? column = prop.GetColumnName(storeObject);
                    if (column == null || have.Contains(column)) continue;
                    if (!prop.IsNullable)
                        throw new InvalidOperationException($"Schema upgrade needs non-nullable column {table}.{column}; add it as nullable or write an explicit migration.");
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {prop.GetColumnType()} NULL";
                    cmd.ExecuteNonQuery();
                    created.Add($"{table}.{column}");
                }
            }

            if (before < CurrentSchemaVersion)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion}";
                cmd.ExecuteNonQuery();
            }

            return new MigrationResult(before, CurrentSchemaVersion, created);
        }
        finally
        {
            if (opened) connection.Close();
        }
    }

    private static int ReadUserVersion(System.Data.Common.DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

public record MigrationResult(int FromVersion, int ToVersion, IReadOnlyList<string> CreatedObjects);
