using System;
using System.Collections.Generic;
using System.Text;

namespace UZIS_Monitor.Services.Interfaces
{
    public interface IFileFormatInfo
    {
        string DisplayName { get; }
        string FileExtension { get; }
        string FileFilter { get; }
    }
}
