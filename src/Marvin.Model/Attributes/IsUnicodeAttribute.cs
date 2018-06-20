using System;

namespace Marvin.Model
{
    /// <summary>
    /// Used to flag a entity property as unicode string
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class IsUnicodeAttribute : Attribute
    {
        /// <summary>
        /// True if the property should be a unicode string
        /// </summary>
        public bool Unicode { get; }

        /// <summary>
        /// Creates a new instance of the <see cref="IsUnicodeAttribute"/>
        /// Flags the propery as a unicode string
        /// </summary>
        public IsUnicodeAttribute() : this(true)
        {
            
        }

        /// <summary>
        /// Creates a new instance of the <see cref="IsUnicodeAttribute"/>
        /// </summary>
        public IsUnicodeAttribute(bool isUnicode)
        {
            Unicode = isUnicode;
        }
    }
}