﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace LiteDB
{
    /// <summary>
    /// Class that converts POCO class to/from BsonDocument
    /// </summary>
    internal class Reflection
    {
        private delegate object CreateObject();

        private static Dictionary<Type, CreateObject> _cacheCtor = new Dictionary<Type,CreateObject>();

        #region GetIdProperty

        /// <summary>
        /// Gets PropertyInfo that refers to Id from a document object.
        /// </summary>
        public static PropertyInfo GetIdProperty(Type type)
        {
            // Get all properties and test in order: BsonIdAttribute, "Id" name, "<typeName>Id" name
            return SelectProperty(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                x => Attribute.IsDefined(x, typeof(BsonIdAttribute), true),
                x => x.Name.Equals("Id", StringComparison.InvariantCultureIgnoreCase),
                x => x.Name.Equals(type.Name + "Id", StringComparison.InvariantCultureIgnoreCase));
        }

        private static PropertyInfo SelectProperty(IEnumerable<PropertyInfo> props, params Func<PropertyInfo, bool>[] predicates)
        {
            foreach (var predicate in predicates)
            {
                var prop = props.FirstOrDefault(predicate);

                if (prop != null)
                {
                    if (!prop.CanRead || !prop.CanWrite)
                    {
                        throw new LiteException(prop.Name + " property must have public get; set;");
                    }

                    return prop;
                }
            }

            return null;
        }

        #endregion

        #region GetProperties

        /// <summary>
        /// Read all properties from a type - store in a static cache - exclude: Id and [BsonIgnore]
        /// </summary>
        public static Dictionary<string, PropertyMapper> GetProperties(Type type, Func<string, string> resolvePropertyName)
        {
            var dict = new Dictionary<string, PropertyMapper>();
            var id = GetIdProperty(type);
            var ignore = typeof(BsonIgnoreAttribute);
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                // ignore indexer property
                if (prop.GetIndexParameters().Length > 0) continue;

                // ignore not read/write
                if (!prop.CanRead || !prop.CanWrite) continue;

                // [BsonIgnore]
                if (prop.IsDefined(ignore, false)) continue;

                // create getter/setter IL function
                var getter = CreateGetMethod(type, prop);
                var setter = CreateSetMethod(type, prop);

                // if not getter or setter - no mapping
                if (getter == null || setter == null) continue;

                var name = id != null && id.Equals(prop) ? "_id" : resolvePropertyName(prop.Name);

                // check if property has [BsonProperty]
                var attr = (BsonPropertyAttribute)prop.GetCustomAttributes(typeof(BsonPropertyAttribute), false).FirstOrDefault();

                if (attr != null) name = attr.Name;

                // create a property mapper
                var p = new PropertyMapper
                { 
                    ResolvedName = name, 
                    PropertyName = prop.Name, 
                    PropertyType = prop.PropertyType,
                    Getter = getter,
                    Setter = setter
                };

                dict.Add(prop.Name, p);
            }

            return dict;
        }

        #endregion

        #region IL Code

        /// <summary>
        /// Create a new instance from a Type
        /// </summary>
        public static object CreateInstance(Type type)
        {
            try
            {
                CreateObject c = null;

                if (_cacheCtor.TryGetValue(type, out c))
                {
                    return c();
                }
                else
                {
                    if (type.IsClass)
                    {
                        var dynMethod = new DynamicMethod("_", type, null);
                        var il = dynMethod.GetILGenerator();
                        il.Emit(OpCodes.Newobj, type.GetConstructor(Type.EmptyTypes));
                        il.Emit(OpCodes.Ret);
                        c = (CreateObject)dynMethod.CreateDelegate(typeof(CreateObject));
                        _cacheCtor.Add(type, c);
                    }
                    else // structs
                    {
                        var dynMethod = new DynamicMethod("_", typeof(object), null);
                        var il = dynMethod.GetILGenerator();
                        var lv = il.DeclareLocal(type);
                        il.Emit(OpCodes.Ldloca_S, lv);
                        il.Emit(OpCodes.Initobj, type);
                        il.Emit(OpCodes.Ldloc_0);
                        il.Emit(OpCodes.Box, type);
                        il.Emit(OpCodes.Ret);
                        c = (CreateObject)dynMethod.CreateDelegate(typeof(CreateObject));
                        _cacheCtor.Add(type, c);
                    }

                    return c();
                }
            }
            catch (Exception)
            {
                throw new LiteException(string.Format("Failed to create instance for type '{0}' from assembly '{1}'",
                    type.FullName, type.AssemblyQualifiedName));
            }
        }

        private static GenericGetter CreateGetMethod(Type type, PropertyInfo propertyInfo)
        {
            var getMethod = propertyInfo.GetGetMethod();
            if (getMethod == null) return null;

            var getter = new DynamicMethod("_", typeof(object), new Type[] { typeof(object) }, type);
            var il = getter.GetILGenerator();

            if (!type.IsClass) // structs
            {
                var lv = il.DeclareLocal(type);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Unbox_Any, type);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, lv);
                il.EmitCall(OpCodes.Call, getMethod, null);
                if (propertyInfo.PropertyType.IsValueType)
                    il.Emit(OpCodes.Box, propertyInfo.PropertyType);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
                il.EmitCall(OpCodes.Callvirt, getMethod, null);
                if (propertyInfo.PropertyType.IsValueType)
                    il.Emit(OpCodes.Box, propertyInfo.PropertyType);
            }

            il.Emit(OpCodes.Ret);

            return (GenericGetter)getter.CreateDelegate(typeof(GenericGetter));
        }

        private static GenericSetter CreateSetMethod(Type type, PropertyInfo propertyInfo)
        {
            var setMethod = propertyInfo.GetSetMethod();
            if (setMethod == null) return null;

            var setter = new DynamicMethod("_", typeof(object), new Type[] { typeof(object), typeof(object) });
            var il = setter.GetILGenerator();

            if (!type.IsClass) // structs
            {
                var lv = il.DeclareLocal(type);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Unbox_Any, type);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, lv);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(propertyInfo.PropertyType.IsClass ? OpCodes.Castclass : OpCodes.Unbox_Any, propertyInfo.PropertyType);
                il.EmitCall(OpCodes.Call, setMethod, null);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Box, type);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(propertyInfo.PropertyType.IsClass ? OpCodes.Castclass : OpCodes.Unbox_Any, propertyInfo.PropertyType);
                il.EmitCall(OpCodes.Callvirt, setMethod, null);
                il.Emit(OpCodes.Ldarg_0);
            }

            il.Emit(OpCodes.Ret);

            return (GenericSetter)setter.CreateDelegate(typeof(GenericSetter));
        }

        #endregion

        #region Utils

        public static bool IsNullable(Type type)
        {
            if (!type.IsGenericType) return false;
            var g = type.GetGenericTypeDefinition();
            return (g.Equals(typeof(Nullable<>)));
        }

        public static Type UnderlyingTypeOf(Type type)
        {
            return type.GetGenericArguments()[0];
        }

        #endregion
    }
}