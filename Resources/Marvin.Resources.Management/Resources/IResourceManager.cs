﻿using Marvin.AbstractionLayer.Resources;
using Marvin.Modules;

namespace Marvin.Resources.Management
{
    /// <summary>
    /// Internal manager within the resource module to handle all resource access
    /// </summary>
    public interface IResourceManager : IInitializablePlugin, IResourceManagement, IResourceCreator
    {
        /// <summary>
        /// Create a new resource instance but DO NOT save it
        /// </summary>
        Resource Create(string type);

        /// <summary>
        /// Get the resource with this id
        /// </summary>
        Resource Get(long id);

        /// <summary>
        /// Write changes on this object to the database
        /// </summary>
        /// <param name="resource"></param>
        void Save(Resource resource);

        /// <summary>
        /// Start the resource with this id
        /// </summary>
        bool Start(Resource resource);

        /// <summary>
        /// Stop the execution of the resource. It will no lange handle incoming messages
        /// </summary>
        bool Stop(Resource resource);
    }
}