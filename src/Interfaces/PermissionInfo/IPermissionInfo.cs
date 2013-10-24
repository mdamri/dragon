﻿using System;

namespace Dragon.Interfaces
{
    public interface IPermissionInfo
    {
        String DisplayName { get; set; }
        String Spec { get; set; }
        bool Inherit { get; set; }
        string InheritedFrom { get; set; }
    }
}