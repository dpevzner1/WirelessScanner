"""DOC-AUDIT-1.5: every code identifier cited by the 1.5 documentation exists in the source; expected topics are documented (read-only)."""
import re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOCS = ["README.md", "CHANGELOG.md", "docs/USER_GUIDE.md", "docs/TECHNICAL_REFERENCE.md", "docs/CONNECTION_TEST_SPEC.md", "docs/STATUS.md",
        "docs/PARITY_CHECKLIST.md", "docs/decisions/DR-0014-connection-quality-testing.md"]
IDS = ["ConnectionTestRunner", "PathDiscovery", "ConnectionAnalyzer", "CONN-ANALYTICS-1", "wirelessscanner.connection_report.v1", "WindowsConnectionProber",
       "IcmpSendEcho2Ex", "BuildDnsQuery", "ParseDnsResponse", "ReadLinkAsync", "ParseWlanStatistics", "ResponderClient", "HttpSpeedClient", "SteadyCounter",
       "SecretProtector", "ConnectionRepository", "ConnectionExportService", "MockConnectionProber", "ResponderProtocol", "ResponderServer",
       "ConnectionTestViewModel", "ConnectionDrawerView", "ConnectionStageView", "ConnectionWpfCharts", "ConnectionSegmentTile", "AttachConnection",
       "StartLinkedSaturationAsync", "UpdateConnectedAsync", "ConnectionStatusProvider", "ModeDescription", "IsConnectionTestRunning", "EstimatedKbps",
       "VerdictThresholds", "OutageRounds", "BaselineWindow", "SpikeFactor", "SpikeMinIncreaseMs", "RunSpeedTestAsync", "RunTwoWayAsync",
       "ConnectionAnalyzerTests", "ConnectionRunnerTests", "ResponderTests", "ConnectionProberTests", "ConnectionUiTests", "ConnectionHardwareRun",
       "ConnectionDiagnostics", "Runs_Ticks_WithDueProbes_BoundToWifiAddress_AndPersistsEverything", "Statistics_MatchHandComputedValues",
       "LinkEvents_RoamNetworkChangeAndSleepGap", "RefusesToStart_WithoutWifiConnection", "SpeedTest_PausesRegularProbes_AndRecordsLatencyUnderLoad",
       "Repository_RoundTrip_AndExports", "TestServers_KeyIsStoredProtected_AndResponderCheckWorks", "SpeedServerKind", "ResponderKey",
       "ConnectionInternetTargets", "ui_conn_running", "ui_conn_setup", "ui_conn_results", "ui_settings_test_servers", "ui_history_connection_tests"]
src = "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in ROOT.rglob("*.cs")
                if not {"GitHub Export", "governance", "obj", "bin"} & set(p.parts))
src += "\n".join(p.read_text(encoding="utf-8", errors="ignore") for p in ROOT.glob("WirelessScanner.Presentation/*.xaml"))
doc_text = "\n".join((ROOT / d).read_text(encoding="utf-8") for d in DOCS)
missing = [i for i in IDS if i not in src and not (ROOT / "docs/images" / (i + ".png")).exists()]
topics = ["Never changes the connection", "Cloudflare", "bufferbloat", "WirelessScanner.Responder", "/api/connection", "DPAPI", "SETTINGS", "attribut",
          "CONNECTION TESTS", "schema 4", "no public", "Refused"]
uncited = [t for t in topics if t.lower() not in doc_text.lower()]
stale = [d for d in DOCS if re.search(r"Version 1\.4\b|# WirelessScanner 1\.4 —|REST API CONFIG tab$", (ROOT / d).read_text(encoding="utf-8"), re.M)]
print(f"identifiers checked: {len(IDS)}; cited in docs: {sum(1 for i in IDS if i in doc_text)}")
print("missing in code:", missing or "none")
print("expected topics missing in docs:", uncited or "none")
print("stale version strings:", stale or "none")
sys.exit(1 if missing or uncited or stale else 0)
