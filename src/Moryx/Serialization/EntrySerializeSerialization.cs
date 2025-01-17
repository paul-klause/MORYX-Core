// Copyright (c) 2023, Phoenix Contact GmbH & Co. KG
// Licensed under the Apache License, Version 2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Moryx.Tools;

namespace Moryx.Serialization
{
    /// <summary>
    /// Filters methods and properties with <see cref="EntrySerializeAttribute"/> set.
    /// </summary>
    public class EntrySerializeSerialization : DefaultSerialization
    {
        private readonly string[] _basePropertyFilter;

        /// <summary>
        /// If set to <c>true</c> explicit properties are filtered
        /// </summary>
        public bool FilterExplicitProperties { get; set; }

        /// <summary>
        /// Default constructor to create a new instance of <see cref="EntrySerializeSerialization"/>
        /// </summary>
        public EntrySerializeSerialization()
        {
            _basePropertyFilter = Array.Empty<string>();
        }

        /// <summary>
        /// Constructor to create a new instance of <see cref="EntrySerializeSerialization"/> with a base filter type.
        /// </summary>
        /// <param name="filterBaseType">All properties of the base type are filtered by default</param>
        public EntrySerializeSerialization(Type filterBaseType)
        {
            _basePropertyFilter = filterBaseType.GetProperties().Select(p => p.Name).ToArray();
        }

        /// <inheritdoc />
        public override IEnumerable<ConstructorInfo> GetConstructors(Type sourceType)
        {
            var constructors = from ctor in base.GetConstructors(sourceType)
                let mode = EvaluateSerializeMode(ctor)
                where mode.HasValue && mode.Value == EntrySerializeMode.Always
                select ctor;

            return constructors;
        }

        /// <inheritdoc />
        public override IEnumerable<MethodInfo> GetMethods(Type sourceType)
        {
            var methods = sourceType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => !m.IsSpecialName);
            methods = from method in methods
                let mode = EvaluateSerializeMode(method)
                where mode.HasValue && mode.Value == EntrySerializeMode.Always
                select method;

            return methods;
        }

        /// <inheritdoc />
        public override IEnumerable<PropertyInfo> GetProperties(Type sourceType)
        {
            var properties = sourceType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(BasePropertyFilter).Where(ExplicitPropertyFilter).ToArray();

            return EntrySerializeAttributeFilter(sourceType, properties);
        }

        /// <inheritdoc />
        public override IEnumerable<MappedProperty> WriteFilter(Type sourceType, IEnumerable<Entry> encoded)
        {
            // Ignore properties which are not mapped
            return base.WriteFilter(sourceType, encoded).Where(mapped => mapped.Entry != null);
        }

        /// <summary>
        /// Property filter for properties of the base class
        /// </summary>
        private bool BasePropertyFilter(PropertyInfo prop)
        {
            if (_basePropertyFilter.Length == 0)
                return true;

            return !_basePropertyFilter.Contains(prop.Name);
        }

        /// <summary>
        /// Property filter for explicit properties
        /// </summary>
        private bool ExplicitPropertyFilter(PropertyInfo prop)
        {
            if (!FilterExplicitProperties)
                return true;

            return !ReflectionTool.IsExplicitInterfaceImplementation(prop);
        }

        private static IEnumerable<PropertyInfo> EntrySerializeAttributeFilter(Type sourceType, PropertyInfo[] properties)
        {
            var sourceTypeMode = EvaluateSerializeMode(sourceType);
            var propertyModes = properties.Select(EvaluateSerializeMode).ToArray();

            var alwaysProperties = propertyModes
                .Where(pm => pm.Mode.HasValue && pm.Mode == EntrySerializeMode.Always)
                .Select(pm => pm.Property).ToArray();

            var neverProperties = propertyModes
                .Where(pm => pm.Mode.HasValue && pm.Mode == EntrySerializeMode.Never)
                .Select(pm => pm.Property).ToArray();

            // Class: Always
            // -> All properties except "Never"
            if (sourceTypeMode.HasValue && sourceTypeMode.Value == EntrySerializeMode.Always)
                return properties.Except(neverProperties);

            // Class: Never
            // -> Only "Always" properties
            if (sourceTypeMode.HasValue && sourceTypeMode.Value == EntrySerializeMode.Never)
                return alwaysProperties;

            // Class: Not defined

            // "Always" and "Never" properties, "Always" properties but no "Never"
            // Only "Never" properties
            if (alwaysProperties.Length > 0)
                return alwaysProperties;

            // No "Always" properties, but "Never" properties
            // -> Properties except "Never"
            if (neverProperties.Length > 0)
                return properties.Except(neverProperties);

            // No property mode defined, No "Always", No "Never"
            // -> All property
            return properties;
        }

        /// <summary>
        /// Checks if the <see cref="EntrySerializeAttribute"/> is existent and activated
        /// </summary>
        private static EntrySerializeMode? EvaluateSerializeMode(ICustomAttributeProvider attributeProvider)
        {
            var entrySerializeAttr = attributeProvider.GetCustomAttribute<EntrySerializeAttribute>(true);
            return entrySerializeAttr?.Mode;
        }

        private static PropertyMode EvaluateSerializeMode(PropertyInfo property)
        {
            return new PropertyMode
            {
                Property = property,
                Mode = EvaluateSerializeMode((ICustomAttributeProvider)property)
            };
        }

        private class PropertyMode
        {
            public PropertyInfo Property { get; set; }

            public EntrySerializeMode? Mode { get; set; }
        }
    }
}
