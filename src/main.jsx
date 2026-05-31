import React, { useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Activity,
  Antenna,
  BadgeCheck,
  BarChart3,
  CirclePause,
  Download,
  FileJson,
  FileSpreadsheet,
  FileText,
  Filter,
  MapPin,
  Pause,
  Play,
  Radar,
  RadioTower,
  RefreshCw,
  Route,
  Search,
  ShieldAlert,
  Signal,
  SlidersHorizontal,
  Target,
  TimerReset,
  Wifi,
  WifiHigh,
  WifiLow,
  WifiZero,
  Zap
} from 'lucide-react';
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  Line,
  LineChart,
  PolarAngleAxis,
  PolarGrid,
  PolarRadiusAxis,
  Radar as RadarShape,
  RadarChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from 'recharts';
import jsPDF from 'jspdf';
import './styles.css';

const interfaces = [
  { id: 'wlan0', name: 'Intel AX211 Wi-Fi 6E', bands: ['2.4 GHz', '5 GHz'], mode: 'Monitor capable' },
  { id: 'usb1', name: 'Alfa AWUS036AXM', bands: ['2.4 GHz', '5 GHz'], mode: 'Survey adapter' },
  { id: 'ethbridge', name: 'Facility Mesh Bridge', bands: ['5 GHz'], mode: 'Passive telemetry' }
];

const palette = ['#0f766e', '#2563eb', '#b45309', '#7c3aed', '#be123c', '#15803d'];
const seededAps = [
  { bssid: '8C:5A:25:04:91:B2', ssid: 'PlantOps', ap: 'AP-North-01', band: '2.4 GHz', channel: 6, base: -57, noise: -91, security: 'WPA3', mesh: 'Production Mesh' },
  { bssid: '8C:5A:25:04:91:B3', ssid: 'PlantOps', ap: 'AP-West-02', band: '5 GHz', channel: 44, base: -64, noise: -94, security: 'WPA3', mesh: 'Production Mesh' },
  { bssid: '8C:5A:25:04:92:10', ssid: 'PlantOps', ap: 'AP-Packout-03', band: '5 GHz', channel: 149, base: -72, noise: -93, security: 'WPA3', mesh: 'Production Mesh' },
  { bssid: 'F0:9F:C2:51:2A:09', ssid: 'Guest-Portal', ap: 'AP-Lobby-01', band: '2.4 GHz', channel: 11, base: -69, noise: -88, security: 'WPA2', mesh: 'Guest Mesh' },
  { bssid: '30:DE:4B:7B:A3:F8', ssid: 'Facilities-IoT', ap: 'AP-Mech-01', band: '2.4 GHz', channel: 1, base: -61, noise: -86, security: 'WPA2', mesh: 'IoT Mesh' },
  { bssid: '30:DE:4B:7B:A3:F9', ssid: 'Facilities-IoT', ap: 'AP-Roof-02', band: '5 GHz', channel: 36, base: -76, noise: -89, security: 'WPA2', mesh: 'IoT Mesh' },
  { bssid: 'A4:BB:6D:19:E2:03', ssid: 'Warehouse-Voice', ap: 'AP-Aisle-07', band: '5 GHz', channel: 157, base: -66, noise: -95, security: 'WPA3', mesh: 'Voice Mesh' },
  { bssid: 'A4:BB:6D:19:E2:04', ssid: 'Warehouse-Voice', ap: 'AP-Dock-04', band: '2.4 GHz', channel: 3, base: -74, noise: -91, security: 'WPA3', mesh: 'Voice Mesh' }
];

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function qualityFromRssi(rssi) {
  return clamp(Math.round(((rssi + 95) / 55) * 100), 0, 100);
}

function bandColor(band) {
  return band === '5 GHz' ? '#2563eb' : '#0f766e';
}

function signalIcon(rssi) {
  if (rssi > -58) return WifiHigh;
  if (rssi > -70) return Wifi;
  if (rssi > -82) return WifiLow;
  return WifiZero;
}

function buildScanTick(tick, travelMode) {
  const wave = Math.sin(tick / 4);
  return seededAps.map((ap, index) => {
    const roamingOffset = travelMode ? Math.sin((tick + index * 3) / 7) * 8 : Math.sin((tick + index) / 5) * 2;
    const jitter = Math.sin(tick * (0.6 + index / 14)) * (index % 3 === 0 ? 4 : 2);
    const rssi = Math.round(clamp(ap.base + roamingOffset + jitter + wave * 2, -94, -38));
    const snr = Math.max(1, rssi - ap.noise);
    return {
      ...ap,
      rssi,
      snr,
      quality: qualityFromRssi(rssi),
      jitter: Math.abs(Math.round(jitter * 10) / 10),
      lastSeen: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
    };
  });
}

function downloadFile(filename, content, type) {
  const blob = new Blob([content], { type });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

function App() {
  const [selectedInterface, setSelectedInterface] = useState(interfaces[0].id);
  const [scanMode, setScanMode] = useState('ssid');
  const [activeFilter, setActiveFilter] = useState('PlantOps');
  const [selectedAp, setSelectedAp] = useState(seededAps[0].bssid);
  const [travelMode, setTravelMode] = useState(true);
  const [isScanning, setIsScanning] = useState(true);
  const [tick, setTick] = useState(0);
  const [aps, setAps] = useState(() => buildScanTick(0, true));
  const [history, setHistory] = useState([]);
  const [surveyPoint, setSurveyPoint] = useState('Zone A / North corridor');
  const [channelScope, setChannelScope] = useState('both');
  const intervalRef = useRef(null);

  useEffect(() => {
    if (!isScanning) return;
    intervalRef.current = window.setInterval(() => setTick((value) => value + 1), 1200);
    return () => window.clearInterval(intervalRef.current);
  }, [isScanning]);

  useEffect(() => {
    const next = buildScanTick(tick, travelMode).filter((ap) => {
      if (channelScope === '2.4') return ap.band === '2.4 GHz';
      if (channelScope === '5') return ap.band === '5 GHz';
      return true;
    });
    setAps(next);
    const watched = next.filter((ap) => (scanMode === 'ssid' ? ap.ssid === activeFilter : ap.bssid === selectedAp));
    setHistory((rows) => {
      const timestamp = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
      const additions = watched.map((ap) => ({
        time: timestamp,
        location: surveyPoint,
        ssid: ap.ssid,
        ap: ap.ap,
        bssid: ap.bssid,
        band: ap.band,
        channel: ap.channel,
        rssi: ap.rssi,
        snr: ap.snr,
        quality: ap.quality,
        jitter: ap.jitter
      }));
      return [...rows, ...additions].slice(-240);
    });
  }, [tick, travelMode, scanMode, activeFilter, selectedAp, surveyPoint, channelScope]);

  const ssids = useMemo(() => [...new Set(aps.map((ap) => ap.ssid))], [aps]);
  const visibleAps = useMemo(() => {
    return aps
      .filter((ap) => (scanMode === 'ssid' ? ap.ssid === activeFilter : ap.bssid === selectedAp))
      .sort((a, b) => b.rssi - a.rssi);
  }, [aps, scanMode, activeFilter, selectedAp]);

  const activeSeries = useMemo(() => {
    const groups = new Map();
    history.forEach((row) => {
      const key = `${row.ap} ${row.band}`;
      if (!groups.has(row.time)) groups.set(row.time, { time: row.time });
      groups.get(row.time)[key] = row.rssi;
    });
    return [...groups.values()].slice(-36);
  }, [history]);

  const bandSummary = useMemo(() => {
    return ['2.4 GHz', '5 GHz'].map((band) => {
      const rows = visibleAps.filter((ap) => ap.band === band);
      const avg = rows.length ? Math.round(rows.reduce((sum, ap) => sum + ap.rssi, 0) / rows.length) : -95;
      return { band, rssi: avg, quality: qualityFromRssi(avg), count: rows.length };
    });
  }, [visibleAps]);

  const health = useMemo(() => {
    const strongest = visibleAps[0];
    const weak = visibleAps.filter((ap) => ap.rssi < -78).length;
    const avgQuality = visibleAps.length
      ? Math.round(visibleAps.reduce((sum, ap) => sum + ap.quality, 0) / visibleAps.length)
      : 0;
    return { strongest, weak, avgQuality };
  }, [visibleAps]);

  const exportRows = history.length ? history : visibleAps.map((ap) => ({
    time: new Date().toISOString(),
    location: surveyPoint,
    ssid: ap.ssid,
    ap: ap.ap,
    bssid: ap.bssid,
    band: ap.band,
    channel: ap.channel,
    rssi: ap.rssi,
    snr: ap.snr,
    quality: ap.quality,
    jitter: ap.jitter
  }));

  function exportCsv() {
    const headers = Object.keys(exportRows[0] || {});
    const csv = [headers.join(','), ...exportRows.map((row) => headers.map((key) => JSON.stringify(row[key] ?? '')).join(','))].join('\n');
    downloadFile('wireless-survey.csv', csv, 'text/csv');
  }

  function exportJson() {
    downloadFile('wireless-survey.json', JSON.stringify({ interface: selectedInterface, mode: scanMode, target: scanMode === 'ssid' ? activeFilter : selectedAp, rows: exportRows }, null, 2), 'application/json');
  }

  function exportPdf() {
    const doc = new jsPDF();
    doc.setFontSize(18);
    doc.text('Wireless Survey Report', 16, 18);
    doc.setFontSize(10);
    doc.text(`Interface: ${interfaces.find((item) => item.id === selectedInterface)?.name}`, 16, 30);
    doc.text(`Target: ${scanMode === 'ssid' ? activeFilter : selectedAp}`, 16, 37);
    doc.text(`Location: ${surveyPoint}`, 16, 44);
    doc.text(`Samples: ${exportRows.length}`, 16, 51);
    doc.setFontSize(12);
    doc.text('Current Access Points', 16, 66);
    visibleAps.slice(0, 12).forEach((ap, index) => {
      doc.text(`${ap.ap} | ${ap.ssid} | ${ap.band} ch ${ap.channel} | ${ap.rssi} dBm | SNR ${ap.snr}`, 16, 77 + index * 8);
    });
    doc.save('wireless-survey.pdf');
  }

  const lineKeys = useMemo(() => [...new Set(history.map((row) => `${row.ap} ${row.band}`))].slice(0, 6), [history]);

  return (
    <main className="app-shell">
      <section className="topbar">
        <div className="brand">
          <div className="brand-mark"><Radar size={24} /></div>
          <div>
            <h1>Wireless Scanner</h1>
            <p>Live mesh survey and signal quality workstation</p>
          </div>
        </div>
        <div className="scan-state">
          <span className={isScanning ? 'pulse-dot' : 'pause-dot'} />
          {isScanning ? 'Scanning' : 'Paused'}
        </div>
      </section>
      <section className="system-note">
        <Search size={16} />
        Browser UI is wired to live survey simulation data. Hardware SSID/AP capture needs a native scanner service for Windows WLAN, Linux monitor mode, or vendor telemetry APIs.
      </section>

      <section className="control-strip">
        <label className="field">
          <span><Antenna size={15} /> Interface</span>
          <select value={selectedInterface} onChange={(event) => setSelectedInterface(event.target.value)}>
            {interfaces.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
          </select>
        </label>

        <div className="segmented" aria-label="Target mode">
          <button className={scanMode === 'ssid' ? 'active' : ''} onClick={() => setScanMode('ssid')}><Wifi size={16} /> SSID</button>
          <button className={scanMode === 'ap' ? 'active' : ''} onClick={() => setScanMode('ap')}><RadioTower size={16} /> AP</button>
        </div>

        {scanMode === 'ssid' ? (
          <label className="field">
            <span><Filter size={15} /> SSID</span>
            <select value={activeFilter} onChange={(event) => setActiveFilter(event.target.value)}>
              {ssids.map((ssid) => <option key={ssid} value={ssid}>{ssid}</option>)}
            </select>
          </label>
        ) : (
          <label className="field wide">
            <span><Target size={15} /> Access point</span>
            <select value={selectedAp} onChange={(event) => setSelectedAp(event.target.value)}>
              {aps.map((ap) => <option key={ap.bssid} value={ap.bssid}>{ap.ap} / {ap.bssid}</option>)}
            </select>
          </label>
        )}

        <label className="field">
          <span><MapPin size={15} /> Survey point</span>
          <input value={surveyPoint} onChange={(event) => setSurveyPoint(event.target.value)} />
        </label>

        <div className="icon-group">
          <button title="Scan both bands" className={channelScope === 'both' ? 'icon active' : 'icon'} onClick={() => setChannelScope('both')}><SlidersHorizontal size={17} /></button>
          <button title="2.4 GHz only" className={channelScope === '2.4' ? 'icon active' : 'icon'} onClick={() => setChannelScope('2.4')}>2.4</button>
          <button title="5 GHz only" className={channelScope === '5' ? 'icon active' : 'icon'} onClick={() => setChannelScope('5')}>5</button>
          <button title={isScanning ? 'Pause scan' : 'Resume scan'} className="icon primary" onClick={() => setIsScanning((value) => !value)}>{isScanning ? <Pause size={17} /> : <Play size={17} />}</button>
          <button title="Reset samples" className="icon" onClick={() => setHistory([])}><TimerReset size={17} /></button>
        </div>
      </section>

      <section className="workspace-grid">
        <aside className="panel sidebar">
          <div className="panel-title">
            <h2>Networks In Range</h2>
            <RefreshCw className={isScanning ? 'spin' : ''} size={16} />
          </div>
          <div className="network-list">
            {aps.map((ap) => {
              const Icon = signalIcon(ap.rssi);
              const active = scanMode === 'ssid' ? ap.ssid === activeFilter : ap.bssid === selectedAp;
              return (
                <button
                  key={ap.bssid}
                  className={active ? 'network-row active' : 'network-row'}
                  onClick={() => {
                    setSelectedAp(ap.bssid);
                    setActiveFilter(ap.ssid);
                  }}
                >
                  <Icon size={20} style={{ color: bandColor(ap.band) }} />
                  <span>
                    <strong>{ap.ssid}</strong>
                    <small>{ap.ap} · {ap.band} · ch {ap.channel}</small>
                  </span>
                  <b>{ap.rssi}</b>
                </button>
              );
            })}
          </div>
        </aside>

        <section className="main-stage">
          <div className="metric-row">
            <Metric icon={Signal} label="Avg quality" value={`${health.avgQuality}%`} tone="green" />
            <Metric icon={RadioTower} label="APs tracked" value={visibleAps.length} tone="blue" />
            <Metric icon={ShieldAlert} label="Weak links" value={health.weak} tone={health.weak ? 'amber' : 'green'} />
            <Metric icon={Zap} label="Best signal" value={health.strongest ? `${health.strongest.rssi} dBm` : '--'} tone="rose" />
          </div>

          <div className="panel graph-panel">
            <div className="panel-title">
              <div>
                <h2>Signal Strength Timeline</h2>
                <p>{travelMode ? 'Travel survey mode' : 'Fixed jitter watch'} · {history.length} samples</p>
              </div>
              <button className={travelMode ? 'toggle active' : 'toggle'} onClick={() => setTravelMode((value) => !value)}>
                {travelMode ? <Route size={16} /> : <CirclePause size={16} />}
                {travelMode ? 'Moving' : 'Stationary'}
              </button>
            </div>
            <div className="chart-tall">
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={activeSeries}>
                  <CartesianGrid stroke="#d7dde5" strokeDasharray="3 3" />
                  <XAxis dataKey="time" tick={{ fontSize: 11 }} minTickGap={24} />
                  <YAxis domain={[-95, -35]} tick={{ fontSize: 11 }} unit=" dBm" />
                  <Tooltip contentStyle={{ borderRadius: 8, borderColor: '#cbd5e1' }} />
                  <Legend />
                  {lineKeys.map((key, index) => (
                    <Line key={key} type="monotone" dataKey={key} stroke={palette[index % palette.length]} dot={false} strokeWidth={2.4} connectNulls />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            </div>
          </div>

          <div className="lower-grid">
            <div className="panel">
              <div className="panel-title">
                <h2>Band Balance</h2>
                <BarChart3 size={16} />
              </div>
              <div className="chart-mid">
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={bandSummary}>
                    <CartesianGrid stroke="#e2e8f0" strokeDasharray="3 3" />
                    <XAxis dataKey="band" tick={{ fontSize: 12 }} />
                    <YAxis domain={[0, 100]} tick={{ fontSize: 11 }} />
                    <Tooltip />
                    <Bar dataKey="quality" radius={[6, 6, 0, 0]}>
                      {bandSummary.map((entry) => <Cell key={entry.band} fill={bandColor(entry.band)} />)}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>

            <div className="panel">
              <div className="panel-title">
                <h2>Mesh Shape</h2>
                <Activity size={16} />
              </div>
              <div className="chart-mid">
                <ResponsiveContainer width="100%" height="100%">
                  <RadarChart data={visibleAps.map((ap) => ({ ap: ap.ap.replace('AP-', ''), quality: ap.quality, snr: ap.snr }))}>
                    <PolarGrid />
                    <PolarAngleAxis dataKey="ap" tick={{ fontSize: 10 }} />
                    <PolarRadiusAxis angle={30} domain={[0, 100]} tick={false} />
                    <RadarShape name="Quality" dataKey="quality" stroke="#0f766e" fill="#0f766e" fillOpacity={0.24} />
                    <RadarShape name="SNR" dataKey="snr" stroke="#2563eb" fill="#2563eb" fillOpacity={0.12} />
                    <Tooltip />
                  </RadarChart>
                </ResponsiveContainer>
              </div>
            </div>
          </div>
        </section>

        <aside className="panel detail">
          <div className="panel-title">
            <h2>Access Point Detail</h2>
            <BadgeCheck size={16} />
          </div>
          <div className="ap-table">
            {visibleAps.map((ap) => (
              <div className="ap-card" key={ap.bssid}>
                <div>
                  <strong>{ap.ap}</strong>
                  <small>{ap.bssid}</small>
                </div>
                <div className="strength-bar">
                  <span style={{ width: `${ap.quality}%`, background: bandColor(ap.band) }} />
                </div>
                <dl>
                  <dt>RSSI</dt><dd>{ap.rssi} dBm</dd>
                  <dt>SNR</dt><dd>{ap.snr} dB</dd>
                  <dt>Band</dt><dd>{ap.band}</dd>
                  <dt>Jitter</dt><dd>{ap.jitter} dB</dd>
                </dl>
              </div>
            ))}
          </div>

          <div className="export-box">
            <h2><Download size={16} /> Export Results</h2>
            <button onClick={exportCsv}><FileSpreadsheet size={16} /> CSV</button>
            <button onClick={exportJson}><FileJson size={16} /> JSON</button>
            <button onClick={exportPdf}><FileText size={16} /> PDF</button>
          </div>
        </aside>
      </section>
    </main>
  );
}

function Metric({ icon: Icon, label, value, tone }) {
  return (
    <div className={`metric ${tone}`}>
      <Icon size={20} />
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

createRoot(document.getElementById('root')).render(<App />);
