﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Text;
using System.Collections;
using System.Data.Common;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
#if EFCore
using Microsoft.EntityFrameworkCore;
#else
using System.Data.Entity;
#endif

namespace EntityFramework.MemoryJoin.Internal
{
    internal static class MappingHelper
    {
        private static readonly ConcurrentDictionary<Type, KnownProvider> TypeToKnownProvider =
            new ConcurrentDictionary<Type, KnownProvider>();

        static int parametersSequence = 0;

        private static void ValidateAndExtendMapping(Dictionary<Type, PropertyInfo[]> mapping)
        {
            //if (!mapping.ContainsKey(typeof(long)))
            //    throw new InvalidOperationException("Please include at least one property with `Long` type");

            //if (!mapping.ContainsKey(typeof(double)))
            //    throw new InvalidOperationException("Please include at least one property with `Double` type");

            if (!mapping.ContainsKey(typeof(int)))
                mapping[typeof(int)] = mapping[typeof(long)];

            if (!mapping.ContainsKey(typeof(Single)))
                mapping[typeof(Single)] = mapping[typeof(Double)];

            if (!mapping.ContainsKey(typeof(decimal)))
                mapping[typeof(decimal)] = mapping[typeof(Double)];

            if (!mapping.ContainsKey(typeof(bool)))
                mapping[typeof(bool)] = mapping[typeof(long)];
        }

        internal static Dictionary<Type, PropertyInfo[]> GetPropertyMappings(Type queryClass)
        {
            var allowedMapping = queryClass.
                GetProperties().
                Where(x => x.GetCustomAttribute<KeyAttribute>() == null).
                GroupBy(x => Nullable.GetUnderlyingType(x.PropertyType) ?? x.PropertyType).
                ToDictionary(x => x.Key, x => x.ToArray());
            ValidateAndExtendMapping(allowedMapping);

            return allowedMapping;
        }

        internal static Mapping<T> GetEntityMapping<T>(
            DbContext context,
            Type queryClass,
            Dictionary<Type, PropertyInfo[]> allowedPropertyMapping
            )
        {
            var allowedProperties = new HashSet<PropertyInfo>(
                allowedPropertyMapping.SelectMany(x => x.Value));

            var inParam = Expression.Parameter(typeof(T), "x");
            var inMappingPairs = new List<Tuple<MemberInfo, Expression>>();

            var outParam = Expression.Parameter(queryClass, "x");
            var outMappingPairs = new List<Tuple<MemberInfo, Expression>>();

            var usedProperties = new Dictionary<string, Func<T, object>>();
            var pkColumnName = EFHelper.GetKeyProperty(context, queryClass);
            if (pkColumnName == null)
                throw new NotSupportedException($"{queryClass.Name} should have PK set");

            var columnNamesDict = EFHelper.GetColumnNames(context, queryClass);
            var members = typeof(T).GetProperties().Cast<MemberInfo>().Union(typeof(T).GetFields());

            foreach (var member in members)
            {
                var memberType = member.MemberType == MemberTypes.Property ?
                    ((PropertyInfo)member).PropertyType :
                    ((FieldInfo)member).FieldType;

                var baseType = Nullable.GetUnderlyingType(memberType) ?? memberType;
                if (!allowedPropertyMapping.TryGetValue(baseType, out var allowedMappedProps))
                    throw new NotSupportedException("Not supported property type");

                var mapProperty = allowedMappedProps.FirstOrDefault(x => allowedProperties.Contains(x));
                if (mapProperty == null)
                    throw new NotSupportedException($"Too complex object, need more properties of '{memberType.Name}' type in '{queryClass.Name}' class");

                Expression inExp = Expression.MakeMemberAccess(inParam, member);
                if (mapProperty.PropertyType != memberType)
                    inExp = Expression.Convert(inExp, mapProperty.PropertyType);

                inMappingPairs.Add(new Tuple<MemberInfo, Expression>(mapProperty, inExp));

                Expression outExp = Expression.MakeMemberAccess(outParam, mapProperty);
                if (mapProperty.PropertyType != memberType)
                {
                    if (memberType == typeof(bool))
                    {
                        outExp = Expression.Equal(Expression.Convert(outExp, typeof(int)), Expression.Constant(1));
                    }
                    else
                    {
                        outExp = Expression.Convert(outExp, memberType);
                    }
                }

                outMappingPairs.Add(new Tuple<MemberInfo, Expression>(member, outExp));

                allowedProperties.Remove(mapProperty);

                usedProperties.Add(
                    columnNamesDict[mapProperty],
                    (Func<T, object>)(Expression.Lambda(
                        Expression.Convert(inExp, typeof(object)),
                        inParam
                    ).Compile()));
            }

            var inCtor = queryClass.GetConstructor(new Type[] { });
            var inNew = Expression.New(inCtor);
            var inBind = Expression.MemberInit(inNew,
                inMappingPairs.Select(x => Expression.Bind(x.Item1, x.Item2)));
            // var inExpression = Expression.Lambda(inBind, inParam);

            var outCtor = typeof(T).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var emptyCtor = outCtor.FirstOrDefault(x => x.GetParameters().Length == 0);
            Expression outExpression;

            if (emptyCtor != null)
            {
                var outNew = Expression.New(emptyCtor);
                var outBind = Expression.MemberInit(outNew,
                    outMappingPairs.Select(x => Expression.Bind(x.Item1, x.Item2)));
                outExpression = Expression.Lambda(outBind, outParam);
            }
            else
            {
                var outNew = Expression.New(
                    outCtor.First(),
                    outMappingPairs.Select(x => x.Item2).ToArray(),
                    outMappingPairs.Select(x => x.Item1).ToArray());
                outExpression = Expression.Lambda(outNew, outParam);
            }

            return new Mapping<T>()
            {
                UserProperties = usedProperties,
                OutExpression = outExpression,
                KeyColumnName = pkColumnName
            };
        }

        internal static void ComposeTableSql(
            StringBuilder stringBuilder,
            InterceptionOptions options,
            DbCommand command,
            IList parameters)
        {
            var providerType = TypeToKnownProvider.GetOrAdd(
                options.ContextType, (t) => GetKnownProvider(command));

            stringBuilder.Append("SELECT * FROM ");

            // SQLite doesn't support column aliasing in the FROM clause. Instead we have to do it
            // in a WITH clause. For example "SELECT * FROM (WITH tempTable(col1, col2, col3) AS (VALUES (1, 2, 3)) SELECT * FROM tempTable)".
            if (providerType == KnownProvider.Sqlite)
            {
                stringBuilder.Append("( WITH ");
                AppendColumnAliases(stringBuilder, options);
                stringBuilder.Append(" AS ");
            }

            if (options.Data.Any())
            {
                AppendRowsAsValues(stringBuilder, options, command, parameters, providerType);
            }
            else
            {
                // if we have no data, we just generate 1 row consisting of null, because we need one row to define a table
                AppendNullRow(stringBuilder, options.ColumnNames.Length + 1); // + 1 since primary key column is not included.
            }

            if (providerType == KnownProvider.Sqlite)
            {
                stringBuilder.Append($" SELECT * FROM {options.DynamicTableName} )");
            }
            else
            {
                stringBuilder.Append("AS ");
                AppendColumnAliases(stringBuilder, options);
            }

            // if we have no data, the in-memory table will consist of a null row. We don't want to return any result. As such,
            // append a condition which will always be false.
            if (!options.Data.Any())
            {
                stringBuilder.Append(" WHERE 1=0");
            }
        }

        private static void AppendRowsAsValues(StringBuilder sb, InterceptionOptions options, DbCommand command, IList parameters, KnownProvider providerType)
        {
#if EFCore
            string paramPattern = EntityFrameworkCore.MemoryJoin.MemoryJoiner.ParametersPrefix;
#else
            string paramPattern = EntityFramework.MemoryJoin.MemoryJoiner.ParametersPrefix;
#endif

            Interlocked.Increment(ref parametersSequence);

            paramPattern = paramPattern + parametersSequence + "_";

            var innerSb = new StringBuilder(20);
            var i = 0;
            var id = 1;

            sb.Append("( VALUES ");

            foreach (var el in options.Data)
            {
                sb.Append("(");
                // Let's append Id anyways, as per Issue #1

                sb.Append(id).Append(", ");
                foreach (var columnName in options.ColumnNames)
                {
                    var value = el[columnName];
                    var stringValue = TryProcessParameterAsString(value,
                        providerType, innerSb, options.ValuesInjectMethod);

                    if (stringValue != null)
                    {
                        sb.Append(stringValue);
                    }
                    else
                    {
                        var paramName = $"{paramPattern}{i}";
                        var param = command.CreateParameter();
                        param.ParameterName = paramName;
                        param.Value = value;
                        parameters.Add(param);
                        sb.Append(paramName);

                        i++;
                    }
                    sb.Append(", ");
                }
                sb.Length -= ", ".Length;
                sb.Append("), ");
                id++;
            }

            sb.Length -= ", ".Length;
            sb.Append(")");
        }

        private static void AppendNullRow(StringBuilder sb, int numberOfColumns)
        {
            sb.Append("(VALUES (");
            for (var i = 0; i < numberOfColumns; i++)
            {
                sb.Append("NULL, ");
            }
            sb.Length -= 2;
            sb.Append("))");
        }

        private static void AppendColumnAliases(StringBuilder sb, InterceptionOptions options)
        {

            sb.Append(options.DynamicTableName)
                .Append("(")
                .Append(options.KeyColumnName)
                .Append(", ");

            foreach (var cname in options.ColumnNames)
            {
                sb.Append(cname).Append(", ");
            }
            sb.Length -= 2;

            sb.Append(")");
        }

        private static string TryProcessParameterAsString(
            object value,
            KnownProvider provider,
            StringBuilder sb,
            ValuesInjectionMethodInternal injectMethod)
        {
            // null is just 'NULL'
            if (value == null)
                return "NULL";

            switch (injectMethod)
            {
                case ValuesInjectionMethodInternal.ViaParameters:
                    return null;
                case ValuesInjectionMethodInternal.Auto:
                    // Postgres has a huge limit for parameters, whereas MSSQL is just ... 2100 :(
                    if (provider == KnownProvider.PostgreSql)
                        return null;
                    break;
            }

            // Try to inject parameters as text
            sb.Length = 0;
            switch (value)
            {
                case string strValue:
                    sb.Append("'").Append(strValue.Replace("'", "''")).Append("'");
                    break;
                case float _:
                case double _:
                case decimal _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case int _:
                case long _:
                    switch (provider)
                    {
                        case KnownProvider.Mssql:
                            if (value is int)
                            {
                                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append("CAST(")
                                    .Append(Convert.ToString(value, CultureInfo.InvariantCulture))
                                    .Append("AS BIGINT)");
                            }
                            break;
                        default:
                            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                            break;
                    }

                    break;
                case DateTime dateValue:
                    switch (provider)
                    {
                        case KnownProvider.Mssql:
                            sb.Append("CAST ('")
                                .Append(dateValue.ToString("yyyy-MM-ddTHH:mm:ss.fff"))
                                .Append("' AS DATETIME)");
                            break;
                        case KnownProvider.PostgreSql:
                            sb.Append("'")
                                .Append(dateValue.ToString("yyyy-MM-ddTHH:mm:ss.fff"))
                                .Append("'::date");
                            break;
                        default:
                            return null;
                    }

                    break;
                case Guid _:

                    switch (provider)
                    {
                        case KnownProvider.PostgreSql:
                                sb.Append("CAST('")
                                    .Append(Convert.ToString(value, CultureInfo.InvariantCulture))
                                    .Append("' AS UUID)");
                            break;
                        case KnownProvider.Mssql:
                            sb.Append("CONVERT(uniqueidentifier, '")
                                    .Append(Convert.ToString(value, CultureInfo.InvariantCulture))
                                    .Append("')");
                            break;
                        default:
                            sb.Append("'").Append(value).Append("'");
                            break;
                    }

                    break;
                default:
                    return null;
            }

            return sb.ToString();
        }

        static KnownProvider GetKnownProvider(DbCommand command)
        {
            var commandTypeName = command.GetType().Name;

            if (commandTypeName.StartsWith("Npgsql"))
                return KnownProvider.PostgreSql;
            if (commandTypeName.StartsWith("SqlCommand"))
                return KnownProvider.Mssql;
            if (commandTypeName.Contains("SqliteCommand"))
                return KnownProvider.Sqlite;

            return KnownProvider.Unknown;
        }

    }
}
