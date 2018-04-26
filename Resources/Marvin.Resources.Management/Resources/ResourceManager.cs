﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Marvin.AbstractionLayer.Capabilities;
using Marvin.AbstractionLayer.Resources;
using Marvin.Container;
using Marvin.Model;
using Marvin.Modules;
using Marvin.Resources.Model;
using Marvin.Tools;

namespace Marvin.Resources.Management
{
    [Plugin(LifeCycle.Singleton, typeof(IResourceManager))]
    internal class ResourceManager : IResourceManager
    {
        #region Dependency Injection

        /// <summary>
        /// Type controller managing the type tree and proxy creation
        /// </summary>
        public IResourceTypeController TypeController { get; set; }

        /// <summary>
        /// Component responsible for linking of the object graph
        /// </summary>
        public IResourceLinker ResourceLinker { get; set; }

        /// <summary>
        /// Access to the database
        /// </summary>
        public IUnitOfWorkFactory UowFactory { get; set; }

        /// <summary>
        /// Error reporting in case a resource crashes
        /// </summary>
        public IModuleErrorReporting ErrorReporting { get; set; }

        /// <summary>
        /// Config of this module
        /// </summary>
        public ModuleConfig Config { get; set; }

        #endregion

        #region Fields

        /// <summary>
        /// Enum of the different startup phases of the resources
        /// </summary>
        private enum ResourceStartupPhase
        {
            /// <summary>
            /// Loading the existing resources or creating a root resource
            /// </summary>
            LoadResources,
            /// <summary>
            /// Calling Initialize() foreach loaded resource
            /// </summary>
            Initializing,
            /// <summary>
            /// Every loaded resource is initialized
            /// </summary>
            Initialized,
            /// <summary>
            /// Calling Start() foreach loaded resource
            /// </summary>
            Starting,
            /// <summary>
            /// Every loaded resource is started.
            /// </summary>
            Started,
            /// <summary>
            /// Calling Stop() foreach resource
            /// </summary>
            Stopping,
            /// <summary>
            /// All resources are stopped
            /// </summary>
            Stopped
        }

        /// <summary>
        /// Current phase of the Resource-Startup-Phase
        /// </summary>
        private ResourceStartupPhase _startup;

        /// <summary>
        /// Direct access to all resources of the tree
        /// </summary>
        private IDictionary<long, ResourceWrapper> _resources;

        /// <summary>
        /// Subset of public resources
        /// </summary>
        private ICollection<IPublicResource> _publicResources;

        #endregion

        #region LifeCycle

        /// <inheritdoc />
        public void Initialize()
        {
            _startup = ResourceStartupPhase.LoadResources;
            using (var uow = UowFactory.Create(ContextMode.AllOff))
            {
                // Create all objects
                var allResources = ResourceEntityAccessor.FetchResourceTemplates(uow);
                if (allResources.Count > 0)
                    LoadResources(allResources);
                else
                    CreateDefault(uow, Config.DefaultResource);
            }

            _startup = ResourceStartupPhase.Initializing;
            // Boot resources
            Parallel.ForEach(_resources.Values, resourceWrapper =>
            {
                try
                {
                    resourceWrapper.Initialize();
                }
                catch (Exception e)
                {
                    resourceWrapper.ErrorOccured();
                    _publicResources.Remove(resourceWrapper.Target as IPublicResource);
                    ErrorReporting.ReportWarning(this, e);
                }
            });
            _startup = ResourceStartupPhase.Initialized;
        }

        /// <summary>
        /// Load and link all resources from the databse
        /// </summary>
        private void LoadResources(ICollection<ResourceEntityAccessor> allResources)
        {
            // Create the concurrent dictionary optimized for the current system architecture and expected collection size
            _resources = new ConcurrentDictionary<long, ResourceWrapper>(Environment.ProcessorCount, allResources.Count * 2);
            _publicResources = new SynchronizedCollection<IPublicResource>();

            // Create resource objects on multiple threads
            var query = from template in allResources.AsParallel()
                        select template.Instantiate(TypeController, this);
            foreach (var resource in query)
                AddResource(resource, false);

            // Link them to each other
            Parallel.ForEach(allResources, LinkReferences);

            // Register events after all links were set
            foreach (var resourceWrapper in _resources.Values)
                RegisterEvents(resourceWrapper.Target);
        }

        /// <summary>
        /// Create root resource if the database is empty
        /// </summary>
        private void CreateDefault(IUnitOfWork uow, string type)
        {
            // Create dictionaries with initial capacity that should avoid the need of resizing
            _resources = new Dictionary<long, ResourceWrapper>(64);
            _publicResources = new SynchronizedCollection<IPublicResource>();

            // Create a root resource
            var defaultResource = Create(type);
            ResourceEntityAccessor.SaveToEntity(uow, defaultResource);
            uow.Save();

            // Add root to the list of resources
            AddResource(defaultResource, true);
        }

        /// <summary>
        /// Add resource to all collections and register to the <see cref="Resource.Changed"/> event
        /// </summary>
        private void AddResource(Resource instance, bool registerEvents)
        {
            var wrapped = new ResourceWrapper(instance);
            // Add to collections
            _resources[instance.Id] = wrapped;
            var publicResource = instance as IPublicResource;
            if (publicResource != null)
                _publicResources.Add(publicResource);

            // Register to events
            if (registerEvents)
                RegisterEvents(instance);

            switch (_startup)
            {
                case ResourceStartupPhase.LoadResources:
                    // do nothing
                    break;
                case ResourceStartupPhase.Initializing:
                case ResourceStartupPhase.Initialized:
                    // Resources those are created during the initialize of a resource are automaticaly initialized also.
                    wrapped.Initialize();
                    break;
                case ResourceStartupPhase.Starting:
                case ResourceStartupPhase.Started:
                    // Resources those are created during the start of a resource are automaticaly initialized and started also.
                    wrapped.Initialize();
                    wrapped.Start();
                    break;
                case ResourceStartupPhase.Stopping:
                case ResourceStartupPhase.Stopped:
                    // do nothing
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // Inform listeners about the new resource
            if (publicResource != null)
                RaiseResourceAdded(publicResource);
        }

        /// <summary>
        /// Register a resources events
        /// </summary>
        private void RegisterEvents(Resource instance)
        {
            instance.Changed += OnResourceChanged;

            var asPublic = instance as IPublicResource;
            if (asPublic != null)
                asPublic.CapabilitiesChanged += RaiseCapabilitiesChanged;

            foreach (var autoSaveCollection in ResourceLinker.GetAutoSaveCollections(instance))
                autoSaveCollection.CollectionChanged += OnAutoSaveCollectionChanged;
        }

        /// <summary>
        /// Register a resources events
        /// </summary>
        private void UnregisterEvents(Resource instance)
        {
            instance.Changed -= OnResourceChanged;

            var asPublic = instance as IPublicResource;
            if (asPublic != null)
                asPublic.CapabilitiesChanged -= RaiseCapabilitiesChanged;

            foreach (var autoSaveCollection in ResourceLinker.GetAutoSaveCollections(instance))
                autoSaveCollection.CollectionChanged -= OnAutoSaveCollectionChanged;
        }

        /// <summary>
        /// Event handler when a resource was modified and the changes need to
        /// written to storage
        /// </summary>
        private void OnResourceChanged(object sender, EventArgs eventArgs)
        {
            Save((Resource)sender);
        }

        /// <summary>
        /// Build object graph from simplified <see cref="ResourceEntityAccessor"/> and flat resource list
        /// </summary>
        private void LinkReferences(ResourceEntityAccessor entityAccessor)
        {
            ResourceLinker.LinkReferences(entityAccessor.Instance, entityAccessor.Relations, _resources);
        }

        ///
        public void Start()
        {
            _startup = ResourceStartupPhase.Starting;
            Parallel.ForEach(_resources.Values, resourceWrapper =>
            {
                try
                {
                    resourceWrapper.Start();
                }
                catch (Exception e)
                {
                    resourceWrapper.ErrorOccured();
                    _publicResources.Remove(resourceWrapper.Target as IPublicResource);
                    ErrorReporting.ReportWarning(this, e);
                }
            });
            _startup = ResourceStartupPhase.Started;
        }

        public void Stop()
        {
            _startup = ResourceStartupPhase.Stopping;
            Parallel.ForEach(_resources.Values, resourceWrapper =>
            {
                try
                {
                    resourceWrapper.Stop();
                }
                catch (Exception e)
                {
                    ErrorReporting.ReportWarning(this, e);
                }
            });
            _startup = ResourceStartupPhase.Stopped;
        }

        ///
        public void Dispose()
        {
            foreach (var resourceWrapper in _resources.Values)
            {
                UnregisterEvents(resourceWrapper.Target);
            }
        }

        #endregion

        public Resource Get(long id) => _resources[id].Target;

        public Resource Create(string type)
        {
            // Create simplified template and instantiate
            var template = new ResourceEntityAccessor();
            template.Name = template.Type = type; // Initially set name to type
            var instance = template.Instantiate(TypeController, this);

            return instance;
        }

        public void Save(Resource resource)
        {
            using (var uow = UowFactory.Create())
            {
                var newResources = new HashSet<Resource>();

                var entity = ResourceEntityAccessor.SaveToEntity(uow, resource);
                if (entity.Id == 0)
                    newResources.Add(resource);

                newResources.AddRange(ResourceLinker.SaveReferences(uow, resource, entity));

                uow.Save();

                foreach (var instance in newResources)
                    AddResource(instance, true);
            }
        }

        /// <summary>
        /// A collection with "AutoSave = true" was modified. Write current state to the database
        /// </summary>
        private void OnAutoSaveCollectionChanged(object sender, ReferenceCollectionChangedEventArgs args)
        {
            var instance = args.Parent;
            var property = args.CollectionProperty;

            using (var uow = UowFactory.Create())
            {
                var newResources = ResourceLinker.SaveSingleCollection(uow, instance, property);
                
                uow.Save();

                foreach (var newResource in newResources)
                    AddResource(newResource, true);
            }
        }

        ///
        public bool Start(Resource resource)
        {
            try
            {
                ((IPlugin)resource).Start();
                return true;
            }
            catch (Exception e)
            {
                ErrorReporting.ReportWarning(this, e);
                return false;
            }
        }

        ///
        public bool Stop(Resource resource)
        {
            try
            {
                ((IPlugin)resource).Stop();
                return true;
            }
            catch (Exception e)
            {
                ErrorReporting.ReportWarning(this, e);
                return false;
            }
        }

        #region IResourceCreator
        public TResource Instantiate<TResource>() where TResource : Resource
        {
            return (TResource)Instantiate(typeof(TResource).Name);
        }

        public TResource Instantiate<TResource>(string type) where TResource : class, IResource
        {
            return Instantiate(type) as TResource;
        }

        public Resource Instantiate(string type)
        {
            return Create(type);
        }

        public bool Destroy(IResource resource)
        {
            return Destroy(resource, false);
        }

        public bool Destroy(IResource resource, bool permanent)
        {
            var instance = (Resource)resource;
            ((IPlugin)resource).Stop();

            // Load entity and relations to disconnect resource and remove from database
            using (var uow = UowFactory.Create())
            {
                var resourceRepository = uow.GetRepository<IResourceEntityRepository>();
                var relationRepository = uow.GetRepository<IResourceRelationRepository>();

                // Fetch entity and relations
                // Update properties on the references and get rid of relation entities
                var entity = resourceRepository.GetByKey(instance.Id);
                var relations = ResourceRelationAccessor.FromEntity(uow, entity);
                foreach (var relation in relations)
                {
                    var reference = _resources[relation.ReferenceId].Target;

                    ResourceLinker.RemoveLinking(resource, reference);

                    if (permanent)
                        relationRepository.Remove(relation.Entity);
                }

                resourceRepository.Remove(entity, permanent);

                uow.Save();
            }

            // Unregister from all events to avoid memory leaks
            UnregisterEvents(instance);

            // Destroy the object
            TypeController.Destroy(instance);

            // Remove from internal collections
            if (_resources.Remove(instance.Id))
            {
                // It can only be a public resource if it was port of the resources
                _publicResources.Remove(resource as IPublicResource);
                return true;
            }
            return false;
        }

        #endregion

        #region IResourceManagement

        public TResource GetResource<TResource>() where TResource : class, IPublicResource
        {
            return GetResource<TResource>(r => true);
        }

        public TResource GetResource<TResource>(long id) where TResource : class, IPublicResource
        {
            return GetResource<TResource>(r => r.Id == id);
        }

        public TResource GetResource<TResource>(string name) where TResource : class, IPublicResource
        {
            return GetResource<TResource>(r => r.Name == name);
        }

        public TResource GetResource<TResource>(ICapabilities requiredCapabilities) where TResource : class, IPublicResource
        {
            return GetResource<TResource>(r => requiredCapabilities.ProvidedBy(r.Capabilities));
        }

        public TResource GetResource<TResource>(Func<TResource, bool> predicate)
            where TResource : class, IPublicResource
        {
            // Public resources without capabilities are considered non-public
            var match = _publicResources.OfType<TResource>().SingleOrDefault(r => r.Capabilities != NullCapabilities.Instance && predicate(r));
            if (match == null)
                throw new ResourceNotFoundException();

            return match;
        }

        public IEnumerable<TResource> GetResources<TResource>() where TResource : class, IPublicResource
        {
            return GetResources<TResource>(r => true);
        }

        public IEnumerable<TResource> GetResources<TResource>(ICapabilities requiredCapabilities) where TResource : class, IPublicResource
        {
            return GetResources<TResource>(r => requiredCapabilities.ProvidedBy(r.Capabilities));
        }

        public IEnumerable<TResource> GetResources<TResource>(Func<TResource, bool> predicate) where TResource : class, IPublicResource
        {
            return _publicResources.OfType<TResource>().Where(r => r.Capabilities != NullCapabilities.Instance).Where(predicate);
        }

        public IReadOnlyList<Resource> GetRoots()
        {
            return _resources.Values.Where(wapper => wapper.Target.Parent == null).Select(wrapper => wrapper.Target).ToArray();
        }

        private void RaiseResourceAdded(IPublicResource newResource)
        {
            ResourceAdded?.Invoke(this, newResource);
        }
        public event EventHandler<IPublicResource> ResourceAdded;

        ///
        public event EventHandler<ICapabilities> CapabilitiesChanged;

        private void RaiseCapabilitiesChanged(object originalSender, ICapabilities capabilities)
        {
            CapabilitiesChanged?.Invoke(originalSender, capabilities);
        }

        #endregion
    }
}