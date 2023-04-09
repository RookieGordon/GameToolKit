using System;

namespace Bonsai
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BonsaiNodeAttribute : Attribute
    {
        public readonly string MenuPath, TexturePath;

        public BonsaiNodeAttribute(string menuPath, string texturePath = null)
        {
            this.MenuPath = menuPath;
            this.TexturePath = texturePath;
        }
    }
}