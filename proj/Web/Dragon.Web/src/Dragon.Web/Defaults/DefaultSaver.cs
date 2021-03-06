﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoMapper;
using Dragon.Data.Attributes;
using Dragon.Data.Interfaces;
using Dragon.Web.Attributes;
using Dragon.Web.Interfaces;
using SimpleInjector;

namespace Dragon.Web.Defaults
{
    public class DefaultSaver<T> : ICommandSave<T>
        where T : CommandBase
    {
        private static Func<T, Guid> m_srcKey;
        private static Func<T, Guid> m_destKey;
        private static Action<T, Guid> m_setDestKey;

        private readonly MethodInfo m_get;
        private readonly MethodInfo m_insert;
        private readonly MethodInfo m_update;

        protected static Type m_persistedAsType;

        private static readonly bool s_active = false;

        private static Func<T, object, object> s_mapping;


        private readonly object RepositoryDestination;

        private Action<object> m_beforeSaveProcessors;
        private Action<object> m_afterSaveProcessors;

        static DefaultSaver()
        {
            var persistedAs = typeof(T).GetCustomAttributes(true).FirstOrDefault(a => a is PersistedAsAttribute);
            m_persistedAsType = null;
            if (persistedAs != null)
            {
                s_active = true;

                var persistedAsAttr = (PersistedAsAttribute)persistedAs;
                m_persistedAsType = persistedAsAttr.As;

                // map command properties to target type directly
                Mapper.CreateMap(typeof(T), m_persistedAsType);

                s_mapping = Mapper.Map;
                //
                // Source key properties
                //
                var srcKeyProps =
                    typeof(T).GetProperties().Where(x => x.GetCustomAttributes(true).Any(a => a is KeyAttribute));
                if (srcKeyProps.Count() != 1)
                    throw new Exception(
                        "DefaultSaver requires one property with Key attribute which TSrc does not have.");

                var srcKeyProperty = srcKeyProps.FirstOrDefault();

                if (!(srcKeyProperty.PropertyType == typeof(Guid)))
                    throw new Exception("DefaultSaver requires a Guid key property which TSrc does not have.");

                m_srcKey = (o) => (Guid)srcKeyProperty.GetValue(o, null);

               
            }
            else if (typeof (T).IsAssignableFromGenericType(typeof (SimpleCommand<>)))
            {
                s_active = true;

                m_persistedAsType = typeof (T).GetGenericArguments()[0];

                var dataProperty = typeof (T).GetProperty("Data").GetMethod;

                s_mapping = (cmd, ignored) => { return dataProperty.Invoke(cmd, null); };

                //
                // Source key properties (for SimpleCommands)
                //
                var srcKeyProps =
                    m_persistedAsType.GetProperties()
                        .Where(x => x.GetCustomAttributes(true).Any(a => a is KeyAttribute));
                if (srcKeyProps.Count() != 1)
                    throw new Exception(
                        "SimpleCommands require a Table with Key attribute which TSrc does not have.");

                var srcKeyProperty = srcKeyProps.FirstOrDefault();

                if (!(srcKeyProperty.PropertyType == typeof (Guid)))
                    throw new Exception(
                        "SimpleCommands require a Table with a Guid key property which TSrc does not have.");

                m_srcKey = (o) => (Guid) srcKeyProperty.GetValue(dataProperty.Invoke(o, null), null);
            }
            else
            {
                throw new Exception(
                    string.Format(
                        "The command {0} must either have a PersistedAs attribute or inherit from SimpleCommand.",
                        typeof (T).FullName));
            }

            //
            // Destination key properties
            //
            var destKeyProps =
                   m_persistedAsType.GetProperties()
                       .Where(x => x.GetCustomAttributes(true).Any(a => a is KeyAttribute));
            if (destKeyProps.Count() != 1)
                throw new Exception(
                    "DefaultSaver requires one property with Key attribute which TDest does not have.");

            var destKeyProperty = destKeyProps.FirstOrDefault();
            
            if (!(destKeyProperty.PropertyType == typeof(Guid)))
                throw new Exception("DefaultSaver requires a Guid key property which TDest does not have.");

            m_destKey = (o) => (Guid)destKeyProperty.GetValue(o, null);
            m_setDestKey = (o, k) => destKeyProperty.SetValue(o, k, null);

        }

        public DefaultSaver(Container container)
        {
            if (!s_active) return;

            var repDestType = typeof(IRepository<>).MakeGenericType(m_persistedAsType);
            RepositoryDestination = container.GetInstance(repDestType);

            foreach (var method in RepositoryDestination.GetType().GetMethods())
            {
                if (!method.IsGenericMethod && method.Name.Equals("Get", StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Count() == 1
                    && method.GetParameters().FirstOrDefault().ParameterType == typeof(object))
                {
                    if (m_get != null)
                    {
                        throw new Exception("Duplicate get method found.");
                    }
                    m_get = method;
                }

                if (!method.IsGenericMethod && method.Name.Equals("Insert", StringComparison.OrdinalIgnoreCase))
                {
                    if (m_insert != null)
                    {
                        throw new Exception("Duplicate insert method found.");
                    }
                    m_insert = method;
                }

                if (!method.IsGenericMethod && method.Name.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {
                    if (m_update != null)
                    {
                        throw new Exception("Duplicate update method found.");
                    }
                    m_update = method;
                }
            }

            var tableBeforeSaveProcessors = container.GetAllInstances(
                typeof(ITableBeforeSave<>).MakeGenericType(m_persistedAsType)).ToList();
            var tableAfterSaveProcessors = container.GetAllInstances(
                typeof(ITableAfterSave<>).MakeGenericType(m_persistedAsType)).ToList();

            var tableBeforeSave = typeof(ITableBeforeSave<>).MakeGenericType(m_persistedAsType);
            var tableAfterSave = typeof(ITableAfterSave<>).MakeGenericType(m_persistedAsType);

            var tableBeforeSaveMethod = tableBeforeSave.GetMethod("BeforeSave");
            var tableAfterSaveMethod = tableAfterSave.GetMethod("AfterSave");

            m_beforeSaveProcessors = (o) =>
            {
                foreach (var p in tableBeforeSaveProcessors)
                {
                    tableBeforeSaveMethod.Invoke(p, new object[] { o });
                }
            };

            m_afterSaveProcessors = (o) =>
            {
                foreach (var p in tableAfterSaveProcessors)
                {
                    tableAfterSaveMethod.Invoke(p, new object[] { o });
                }
            };
        }

        public virtual void Save(T obj)
        {
            if (!s_active) return;

            var dest = m_get.Invoke(RepositoryDestination, new object[] { m_srcKey(obj) });

            var newObject = (dest == null);

            if (newObject)
            {
                dest = Activator.CreateInstance(m_persistedAsType);
            }

            dest = s_mapping(obj, dest);
            
            m_beforeSaveProcessors(dest);

            if (newObject)
            {
                m_insert.Invoke(RepositoryDestination, new object[] { dest });
            }
            else
            {
                m_update.Invoke(RepositoryDestination, new object[] { dest });
            }

            m_afterSaveProcessors(dest);

        }

    }
}
