"""DOC-AUDIT-1.4: every code identifier cited by the 1.4 documentation exists in the source (read-only)."""
import re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOCS = ["README.md", "CHANGELOG.md", "docs/USER_GUIDE.md", "docs/TECHNICAL_REFERENCE.md", "docs/SATURATION_TEST_SPEC.md",
        "docs/STATUS.md", "docs/PARITY_CHECKLIST.md", "docs/decisions/DR-0013-select-by-access-point-and-mesh.md"]
IDS = ["PhysicalUnitGrouper", "IUnitResolver", "UnitOverrides", "SaturationUnitTarget", "SaturationUnitSummary", "SaturationRadioSummary",
       "TsfOffsetMs", "ParseWps", "WithWps", "SameRadioToleranceMs", "Retention", "SameFamily", "ToggleUnit", "StateOfUnit", "IsUnitSelected",
       "UnitBssids", "UnitResolver", "SingleUnit", "MultipleUnits", "TargetUnits", "ResolveUnits", "AnalyzeUnit", "UnitConsistencyFindingDb",
       "SsidOnlyOutageFindingPct", "UnitSummaries", "UnitDefinition", "UnitOverridesSettingKey", "ExplorerGrouping", "GroupingIndex", "UnitGroups",
       "UnitGroupItem", "ToggleUnitCommand", "ToggleUnitExpandCommand", "RenameUnit", "SplitAp", "MoveAp", "ResetUnit", "MoveTargets",
       "CanEditUnits", "UnitRow_ContextMenuOpening", "UnitMember_ContextMenuOpening", "HardwareIeInventoryProbe",
       "PhysicalUnitTests", "PhysicalUnitIntegrationTests", "SplitMergeRename_ArePersistedAndWin", "Overrides_PersistInSettings_AndReloadOnStart",
       "Explorer_AccessPointView_SelectsUnit_AndTestConfigResolvesItAtStart", "Tracker_UnitScope_TracksMembersFromFirstCycle_WithSsids",
       "Reports_CarryUnitSection_InJsonCsvAndPdf_AndNothingExtraWithoutUnits", "ApiSettingsScreenTests", "NetworkSelectionTests",
       "PhysicalUnitOverrides", "SAT-ANALYTICS-3", "wirelessscanner.saturation_report.v3", "ui_ls_explorer_access_points"]
src = "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in ROOT.rglob("*.cs")
                if "GitHub Export" not in p.parts and "governance" not in p.parts and "obj" not in p.parts and "bin" not in p.parts)
src += "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in ROOT.glob("WirelessScanner.Presentation/*.xaml"))
doc_text = "\n".join((ROOT / d).read_text(encoding="utf-8") for d in DOCS)
missing_code = [i for i in IDS if i not in src and not (ROOT / "docs/images" / (i + ".png")).exists()]
cited = [i for i in IDS if i in doc_text]
uncited = [i for i in ["PhysicalUnitGrouper", "AnalyzeUnit", "SingleUnit", "_units.csv", "6b"] if i not in doc_text]
stale = [d for d in DOCS if re.search(r"Version 1\.2\b|saturation_report\.v2`:|SAT-ANALYTICS-2`, DR-0012\)$", (ROOT / d).read_text(encoding="utf-8"), re.M)]
print(f"identifiers checked: {len(IDS)}; cited in docs: {len(cited)}")
print("missing in code:", missing_code or "none")
print("expected doc topics missing:", uncited or "none")
print("stale version strings:", stale or "none")
sys.exit(1 if missing_code or uncited or stale else 0)
