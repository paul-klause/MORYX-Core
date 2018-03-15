﻿using Marvin.Model;

namespace Marvin.Products.Model
{
    /// <summary>
    /// The public API of the ProductDocument repository.
    /// </summary>
    public interface IProductDocumentRepository : IRepository<ProductDocument>
    {
        /// <summary>
        /// Creates instance with all not nullable properties prefilled
        /// </summary>
        ProductDocument Create(string fileName, string filesystemPath); 
    }
}
