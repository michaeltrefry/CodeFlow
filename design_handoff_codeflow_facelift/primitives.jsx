// primitives.jsx — reusable UI atoms

const { useState, useEffect, useRef, useMemo, useCallback, Fragment } = React;

// ---------- Icons (hand-crafted single-glyph SVGs) ----------
const Ico = {
  traces:    (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M2 12h2l2-6 2 8 2-5 2 3h2" /></svg>,
  agents:    (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="3" y="3" width="5" height="5" rx="1" /><rect x="8" y="8" width="5" height="5" rx="1" /><rect x="8" y="3" width="5" height="5" rx="1" /><rect x="3" y="8" width="5" height="5" rx="1" /></svg>,
  workflows: (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><circle cx="3.5" cy="8" r="1.5"/><circle cx="8" cy="3.5" r="1.5"/><circle cx="8" cy="12.5" r="1.5"/><circle cx="12.5" cy="8" r="1.5"/><path d="M4.8 7 6.7 4.8M9.3 4.8l1.9 2.2M11.2 9l-1.9 2.2M6.7 11.2 4.8 9"/></svg>,
  hitl:      (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><circle cx="8" cy="5.5" r="2.5"/><path d="M3 13.5c.9-2.5 2.8-4 5-4s4.1 1.5 5 4"/></svg>,
  dlq:       (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h7"/><path d="M12 11.5 13.5 13l2-2.5" stroke="currentColor"/></svg>,
  settings:  (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><circle cx="8" cy="8" r="2" /><path d="M8 1.5v2M8 12.5v2M14.5 8h-2M3.5 8h-2M12.6 3.4l-1.4 1.4M4.8 11.2l-1.4 1.4M12.6 12.6l-1.4-1.4M4.8 4.8 3.4 3.4"/></svg>,
  mcp:       (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="2" y="3" width="12" height="4" rx="1"/><rect x="2" y="9" width="12" height="4" rx="1"/><circle cx="4.5" cy="5" r=".6" fill="currentColor"/><circle cx="4.5" cy="11" r=".6" fill="currentColor"/></svg>,
  roles:     (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M8 1.5 2.5 4v4.5c0 3 2.3 5.4 5.5 6 3.2-.6 5.5-3 5.5-6V4L8 1.5z"/></svg>,
  skills:    (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="m8 2 2 4 4.4.5-3.2 3.1.9 4.4L8 11.8 3.9 14l.9-4.4L1.6 6.5 6 6z"/></svg>,
  git:       (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><circle cx="3.5" cy="8" r="1.5"/><circle cx="12.5" cy="4" r="1.5"/><circle cx="12.5" cy="12" r="1.5"/><path d="M3.5 6.5V4c0-1 1-1.5 2-1.5h3.5M3.5 9.5v2c0 1 1 1.5 2 1.5h5"/></svg>,
  inventory: (p) => <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="2" y="2" width="5" height="5" rx="1"/><rect x="9" y="2" width="5" height="5" rx="1"/><rect x="2" y="9" width="5" height="5" rx="1"/><rect x="9" y="9" width="5" height="5" rx="1"/></svg>,
  search:    (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" {...p}><circle cx="6" cy="6" r="4"/><path d="m9 9 3 3"/></svg>,
  bell:      (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M3 10V6.5C3 4.6 4.6 3 6.5 3h1C9.4 3 11 4.6 11 6.5V10"/><path d="M2 10h10M6 12h2"/></svg>,
  plus:      (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" {...p}><path d="M7 2.5v9M2.5 7h9"/></svg>,
  chevL:     (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="m8.5 3.5-3.5 3.5 3.5 3.5"/></svg>,
  chevR:     (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="m5.5 3.5 3.5 3.5-3.5 3.5"/></svg>,
  chevD:     (p) => <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="m3 4.5 3 3 3-3"/></svg>,
  close:     (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" {...p}><path d="m3 3 8 8M11 3l-8 8"/></svg>,
  panelL:    (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" {...p}><rect x="2" y="2.5" width="10" height="9" rx="1"/><path d="M6 2.5v9"/></svg>,
  trash:     (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M2.5 3.5h9M4 3.5V2.5h6V3.5M4.5 3.5v8a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-8"/></svg>,
  play:      (p) => <svg width="12" height="12" viewBox="0 0 12 12" fill="currentColor" {...p}><path d="M3.5 2v8l6-4z"/></svg>,
  check:     (p) => <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="m2.5 6.5 2.5 2.5 4.5-5"/></svg>,
  x:         (p) => <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" {...p}><path d="m2 2 6 6M8 2l-6 6"/></svg>,
  alert:     (p) => <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M6 2v4M6 8.5v.1"/><circle cx="6" cy="6" r="5"/></svg>,
  logic:     (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="m2 7 5-4 5 4-5 4-5-4z"/></svg>,
  bot:       (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="2.5" y="4.5" width="9" height="7" rx="1.5"/><path d="M7 2.5v2M5 7.5v.5M9 7.5v.5"/></svg>,
  copy:      (p) => <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><rect x="3.5" y="3.5" width="6.5" height="6.5" rx="1"/><path d="M3.5 8V2.5c0-.5.5-1 1-1H8"/></svg>,
  refresh:   (p) => <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M10.5 6A4.5 4.5 0 1 1 9 2.8"/><path d="M10.5 1.5v2.5H8"/></svg>,
  back:      (p) => <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" {...p}><path d="M6 3 2 7l4 4M2 7h10"/></svg>
};

// ---------- Button ----------
function Button({ children, variant = 'default', size = 'md', icon, onClick, ...rest }) {
  const cls = ['btn'];
  if (variant === 'primary') cls.push('btn-primary');
  if (variant === 'ghost') cls.push('btn-ghost');
  if (variant === 'danger') cls.push('btn-danger');
  if (size === 'sm') cls.push('btn-sm');
  if (size === 'lg') cls.push('btn-lg');
  return (
    <button className={cls.join(' ')} onClick={onClick} {...rest}>
      {icon}
      {children && <span>{children}</span>}
    </button>
  );
}

// ---------- Chip ----------
function Chip({ variant = 'default', dot, mono, square, children, ...rest }) {
  const cls = ['chip'];
  if (variant !== 'default') cls.push(variant);
  if (mono) cls.push('mono');
  if (square) cls.push('square');
  return (
    <span className={cls.join(' ')} {...rest}>
      {dot && <span className="chip-dot" />}
      {children}
    </span>
  );
}

function StateChip({ state }) {
  if (state === 'Completed') return <Chip variant="ok" dot>Completed</Chip>;
  if (state === 'Running')   return <Chip variant="running" dot>Running</Chip>;
  if (state === 'Failed')    return <Chip variant="err" dot>Failed</Chip>;
  if (state === 'Escalated') return <Chip variant="warn" dot>Escalated</Chip>;
  return <Chip dot>{state}</Chip>;
}

// ---------- Card ----------
function Card({ title, right, children, flush }) {
  return (
    <div className="card">
      {(title || right) && (
        <div className="card-header">
          {title && <h3>{title}</h3>}
          {right}
        </div>
      )}
      <div className={flush ? 'card-body card-body-flush' : 'card-body'}>{children}</div>
    </div>
  );
}

// ---------- Segmented control ----------
function Segmented({ value, onChange, options }) {
  return (
    <div className="seg">
      {options.map(o => {
        const v = typeof o === 'string' ? o : o.value;
        const label = typeof o === 'string' ? o : o.label;
        return <button key={v} data-active={value === v} onClick={() => onChange(v)}>{label}</button>;
      })}
    </div>
  );
}

// ---------- Tabs ----------
function Tabs({ value, onChange, items }) {
  return (
    <div className="tabs">
      {items.map(it => (
        <button key={it.value} className="tab" data-active={value === it.value} onClick={() => onChange(it.value)}>
          {it.label}
          {typeof it.count === 'number' && <span className="tab-count">{it.count}</span>}
        </button>
      ))}
    </div>
  );
}

// ---------- Provider icon ----------
function ProviderIco({ provider }) {
  if (!provider) return null;
  const letter = provider === 'openai' ? 'O' : provider === 'anthropic' ? 'A' : 'L';
  return <span className={`provider-ico ${provider}`}>{letter}</span>;
}

// ---------- Field ----------
function Field({ label, hint, children, className = '' }) {
  return (
    <label className={`field ${className}`}>
      {label && <span className="field-label">{label}</span>}
      {children}
      {hint && <span className="field-hint">{hint}</span>}
    </label>
  );
}

// ---------- Empty state ----------
function Empty({ icon, title, desc, action }) {
  return (
    <div className="empty">
      <div className="empty-ico">{icon}</div>
      <h4>{title}</h4>
      {desc && <div>{desc}</div>}
      {action}
    </div>
  );
}

// ---------- Page header ----------
function PageHeader({ title, subtitle, actions }) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {actions && <div className="page-header-actions">{actions}</div>}
    </div>
  );
}

// ---------- Time formatting ----------
function relTime(iso) {
  const t = new Date(iso).getTime();
  const diff = (Date.now() - t) / 1000;
  if (diff < 60) return `${Math.max(0, Math.round(diff))}s ago`;
  if (diff < 3600) return `${Math.round(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.round(diff / 3600)}h ago`;
  return `${Math.round(diff / 86400)}d ago`;
}
function fmtDate(iso) {
  const d = new Date(iso);
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

Object.assign(window, { Ico, Button, Chip, StateChip, Card, Segmented, Tabs, ProviderIco, Field, Empty, PageHeader, relTime, fmtDate });
