﻿using System;
using System.Collections.Generic;
using System.Text;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core.Attributes;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.Weapons
{
    [NetFieldExportGroup("/Game/Weapons/FORT_Pistols", ParseType.Full, true)]
    public class Pistols : Weapon
    {
    }
}