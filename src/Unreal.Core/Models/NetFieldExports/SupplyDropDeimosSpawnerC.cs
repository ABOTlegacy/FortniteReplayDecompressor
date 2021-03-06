using System.Collections.Generic;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models.Enums;

namespace Unreal.Core.Models
{
	[NetFieldExportGroup("/Game/Athena/Deimos/Spawners/RiftSpawners/AthenaSupplyDrop_DeimosSpawner.AthenaSupplyDrop_DeimosSpawner_C")]
	public class SupplyDropDeimosSpawnerC : INetFieldExportGroup
	{
		[NetFieldExport("ReplicatedMovement", RepLayoutCmdType.RepMovement, 5, "ReplicatedMovement", "FRepMovement", 83)]
		public FRepMovement ReplicatedMovement { get; set; } //Type: FRepMovement Bits: 83

		[NetFieldExport("bEditorPlaced", RepLayoutCmdType.PropertyBool, 26, "bEditorPlaced", "uint8", 1)]
		public bool? bEditorPlaced { get; set; } //Type: uint8 Bits: 1

	}
}
