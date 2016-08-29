﻿#region License
//
// Servant
//
// Copyright 2016 Drew Noakes
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
// More information about this project is available at:
//
//    https://github.com/drewnoakes/servant
//
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;

namespace Servant
{
    /// <summary>
    /// Specifies how instances are reused between dependants.
    /// </summary>
    public enum Lifestyle
    {
        /// <summary>
        /// Only a single instance of the service will be created.
        /// </summary>
        Singleton,

        /// <summary>
        /// A new instance of the service will be created for each dependant.
        /// </summary>
        Transient
    }

    internal sealed class TypeEntry
    {
        public Type DeclaredType { get; }

        [CanBeNull] public TypeProvider Provider { get; set; }

        public TypeEntry(Type declaredType)
        {
            DeclaredType = declaredType;
        }
    }

    internal sealed class TypeProvider
    {
        public Lifestyle Lifestyle { get; }
        public IReadOnlyList<TypeEntry> Dependencies { get; }

        private readonly Servant _servant;
        private readonly Func<object[], Task<object>> _factory;
        private readonly Type _declaredType;

        [CanBeNull] private object _singletonInstance;

        public TypeProvider(Servant servant, Func<object[], Task<object>> factory, Type declaredType, Lifestyle lifestyle, IReadOnlyList<TypeEntry> dependencies)
        {
            _servant = servant;
            _factory = factory;
            _declaredType = declaredType;
            Lifestyle = lifestyle;
            Dependencies = dependencies;
        }

        public async Task<object> GetAsync()
        {
            // TODO make concurrency-safe here to avoid double-allocation of singleton

            if (Lifestyle == Lifestyle.Singleton && _singletonInstance != null)
                return _singletonInstance;

            // find arguments
            var argumentTasks = new List<Task<object>>();
            foreach (var dep in Dependencies)
            {
                if (dep.Provider == null)
                {
                    // No provider exists for this dependency.
                    var message = $"Type \"{_declaredType}\" depends upon unregistered type \"{dep.DeclaredType}\".";

                    // See whether we have a super-type of the requested type.
                    var superTypes = _servant.GetRegisteredTypes().Where(type => type.IsAssignableFrom(dep.DeclaredType)).ToList();
                    if (superTypes.Any())
                        message += $" Did you mean to reference registered super type {string.Join(" or ", superTypes.Select(st => $"\"{st}\""))}?";

                    throw new ServantException(message);
                }
                argumentTasks.Add(dep.Provider.GetAsync());
            }

            await Task.WhenAll(argumentTasks);

            var instance = await _factory.Invoke(argumentTasks.Select(t => t.Result).ToArray());

            if (instance == null)
                throw new ServantException($"Instance for type \"{_declaredType}\" cannot be null.");

            if (!_declaredType.IsInstanceOfType(instance))
                throw new ServantException($"Instance produced for type \"{_declaredType}\" is not an instance of that type.");

            if (Lifestyle == Lifestyle.Singleton)
            {
                _singletonInstance = instance;

                var disposable = instance as IDisposable;
                if (disposable != null)
                    _servant.PushDisposableSingleton(disposable);
            }

            return instance;
        }
    }

    /// <summary>
    /// Serves instances of specific types, resolving dependencies as required, and running any async initialisation.
    /// </summary>
    /// <remarks>
    /// Disposing this class will dispose any contained singleton instances that implement <see cref="IDisposable"/>.
    /// Transient instances are not tracked by this class and must be disposed by their consumers.
    /// </remarks>
    public sealed class Servant : IDisposable
    {
        private readonly ConcurrentDictionary<Type, TypeEntry> _entryByType = new ConcurrentDictionary<Type, TypeEntry>();
        private readonly ConcurrentStack<IDisposable> _disposableSingletons = new ConcurrentStack<IDisposable>();

        private int _disposed;

        private TypeEntry GetOrAddTypeEntry(Type declaredType) => _entryByType.GetOrAdd(declaredType, t => new TypeEntry(t));

        /// <summary>
        /// Adds the means of obtaining an instance of type <paramref name="declaredType"/>.
        /// </summary>
        /// <param name="lifestyle">Specifies how instances are reused between dependants.</param>
        /// <param name="declaredType">The <see cref="Type"/> via which instances must be requested.</param>
        /// <param name="factory">A function that returns an instance of <paramref name="declaredType"/> given a set of dependencies.</param>
        /// <param name="parameterTypes">The types of dependencies required by <paramref name="factory"/>.</param>
        public void Add(Lifestyle lifestyle, Type declaredType, Func<object[], Task<object>> factory, Type[] parameterTypes)
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(Servant));

            // Validate the type doesn't depend upon itself
            if (parameterTypes.Contains(declaredType))
                throw new ServantException($"Type \"{declaredType}\" depends upon its own type, which is disallowed.");

            // Validate no duplicate parameter types
            var dupes = parameterTypes.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count != 0)
                throw new ServantException($"Type \"{declaredType}\" has multiple dependencies upon type{(dupes.Count == 1 ? "" : "s")} {string.Join(", ", dupes.Select(t => $"\"{t}\""))}, which is disallowed.");

            // Validate we won't end up creating a cycle
            foreach (var parameterType in parameterTypes)
            {
                TypeEntry parameterTypeEntry;
                if (!_entryByType.TryGetValue(parameterType, out parameterTypeEntry))
                    continue;

                // Creates a cycle if one of parameterTypes depends upon declaredType
                if (DependsUpon(parameterTypeEntry, declaredType))
                    throw new ServantException($"Type \"{declaredType}\" cannot depend upon type \"{parameterType}\" as this would create circular dependencies.");
            }

            var typeEntry = GetOrAddTypeEntry(declaredType);

            if (typeEntry.Provider != null)
                throw new ServantException($"Type \"{declaredType}\" already registered.");

            typeEntry.Provider = new TypeProvider(this, factory, declaredType, lifestyle, parameterTypes.Select(GetOrAddTypeEntry).ToList());
        }

        private static bool DependsUpon(TypeEntry dependant, Type dependent)
        {
            if (dependant.Provider == null)
                return false;

            foreach (var dependency in dependant.Provider.Dependencies)
            {
                if (dependency.DeclaredType == dependent)
                    return true;

                if (DependsUpon(dependency, dependent))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Eagerly initialises all types registered as having <see cref="Lifestyle.Singleton"/> lifestyle.
        /// </summary>
        /// <remarks>
        /// Calling this method is optional. If not used, singletons will be initialised lazily, when first requested.
        /// <para />
        /// If all singletons are already instantiated, calling this method has no effect.
        /// </remarks>
        /// <returns>A task that completes when singleton initialisation has finished.</returns>
        public Task CreateSingletonsAsync()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(Servant));

            return Task.WhenAll(
                from typeEntry in _entryByType.Values
                let provider = typeEntry.Provider
                where provider?.Lifestyle == Lifestyle.Singleton
                select provider.GetAsync());
        }

        /// <summary>
        /// Serves an instance of type <typeparamref name="T"/>, performing any requires async initialisation.
        /// </summary>
        /// <typeparam name="T">The type to be served.</typeparam>
        /// <returns>A task that completes when the instance is ready.</returns>
        public Task<T> ServeAsync<T>()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(Servant));

            TypeEntry entry;
            if (!_entryByType.TryGetValue(typeof(T), out entry) || entry.Provider == null)
                throw new ServantException($"Type \"{typeof(T)}\" is not registered.");

            return TaskUtil.Upcast<T>(entry.Provider.GetAsync());
        }

        /// <summary>
        /// Gets a value indicating whether the type <typeparamref name="T"/> has been registered with this servant.
        /// </summary>
        /// <remarks>
        /// Just because a type is registered, does not mean it can be served. It may depend upon other
        /// types which have not been registered.
        /// <para />
        /// If a type is known only as a dependency of a type passed to one of the <c>Add</c> methods,
        /// then this method returns <c>false</c>.
        /// </remarks>
        /// <typeparam name="T">The type to test for.</typeparam>
        /// <returns><c>true</c> if the type has been registered, otherwise <c>false</c>.</returns>
        public bool IsTypeRegistered<T>() => IsTypeRegistered(typeof(T));

        /// <summary>
        /// Gets a value indicating whether the type <paramref name="type"/> has been registered with this servant.
        /// </summary>
        /// <remarks>
        /// Just because a type is registered, does not mean it can be served. It may depend upon other
        /// types which have not been registered.
        /// <para />
        /// If a type is known only as a dependency of a type passed to one of the <c>Add</c> methods,
        /// then this method returns <c>false</c>.
        /// </remarks>
        /// <param name="type">The type to test for.</param>
        /// <returns><c>true</c> if the type has been registered, otherwise <c>false</c>.</returns>
        public bool IsTypeRegistered(Type type)
        {
            TypeEntry entry;
            return _entryByType.TryGetValue(type, out entry) && entry.Provider != null;
        }

        /// <summary>
        /// Gets all types registered with this servant.
        /// </summary>
        /// <returns>An enumeration of registered types.</returns>
        public IEnumerable<Type> GetRegisteredTypes()
        {
            return from entry in _entryByType.Values
                   where entry.Provider != null
                   select entry.DeclaredType;
        }

        internal void PushDisposableSingleton(IDisposable disposableSingletonInstance)
        {
            // We push disposable instances onto a stack and dispose them in reverse order
            _disposableSingletons.Push(disposableSingletonInstance);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            // TODO catch exceptions and throw an aggregate?
            IDisposable disposable;
            while (_disposableSingletons.TryPop(out disposable))
                disposable.Dispose();
        }
    }

    /// <summary>
    /// An exception raised by Servant.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public sealed class ServantException : Exception
    {
        /// <inheritdoc />
        public ServantException() { }

        /// <inheritdoc />
        public ServantException(string message) : base(message) { }

        /// <inheritdoc />
        public ServantException(string message, Exception innerException) : base(message, innerException) { }
    }
}
