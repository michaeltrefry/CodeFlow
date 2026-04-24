// pages-traces.jsx — Traces list + Trace detail

function TracesPage({ onOpenTrace }) {
  const [hideChildren, setHideChildren] = useState(false);
  const [bulkState, setBulkState] = useState('Completed');
  const [olderThan, setOlderThan] = useState(7);
  const traces = CF_DATA.TRACES.filter(t => !hideChildren || !t.parentTraceId);

  return (
    <div className="page">
      <PageHeader
        title="Traces"
        subtitle="Every workflow run, streamed in real time. Running traces update live."
        actions={<>
          <Button variant="ghost" icon={<Ico.refresh />}>Refresh</Button>
          <Button variant="primary" icon={<Ico.plus />}>Submit run</Button>
        </>}
      />

      <div className="bulk-panel">
        <div>
          <div className="label">Bulk cleanup</div>
          <div className="muted">Delete terminal traces older than a cutoff. Running traces are always excluded.</div>
        </div>
        <div className="bulk-controls">
          <div className="bulk-field">
            <span className="field-label">State</span>
            <select className="select" value={bulkState} onChange={e => setBulkState(e.target.value)}>
              <option value="Completed">Completed traces</option>
              <option value="Failed">Failed traces</option>
              <option value="Escalated">Escalated traces</option>
              <option value="">All terminal traces</option>
            </select>
          </div>
          <div className="bulk-field">
            <span className="field-label">Older than</span>
            <select className="select" value={olderThan} onChange={e => setOlderThan(+e.target.value)}>
              <option value={1}>1 day</option>
              <option value={7}>7 days</option>
              <option value={30}>30 days</option>
              <option value={90}>90 days</option>
            </select>
          </div>
          <Button variant="danger" icon={<Ico.trash />}>Delete completed</Button>
        </div>
      </div>

      <div className="list-toolbar">
        <div className="list-toolbar-left">
          <Segmented value="all" onChange={() => {}} options={[
            { value: 'all', label: 'All' },
            { value: 'running', label: 'Running' },
            { value: 'terminal', label: 'Terminal' }
          ]} />
          <label className="checkbox">
            <input type="checkbox" checked={hideChildren} onChange={e => setHideChildren(e.target.checked)} />
            <span>Hide subflow children</span>
          </label>
        </div>
        <span className="muted small">{traces.length} of {CF_DATA.TRACES.length} shown</span>
      </div>

      <div className="card" style={{overflow:'hidden'}}>
        <div className="scroll" style={{maxHeight:'calc(100vh - 390px)'}}>
          <table className="table">
            <thead><tr>
              <th>Trace</th><th>Workflow</th><th>Current agent</th><th>State</th>
              <th>Round</th><th>Updated</th><th></th>
            </tr></thead>
            <tbody>
              {traces.map(t => (
                <tr key={t.traceId} onClick={() => onOpenTrace(t)}>
                  <td>
                    <span className="mono-link">{t.traceId.slice(0,8)}</span>
                    {t.parentTraceId && <Chip style={{marginLeft:6}}>child</Chip>}
                  </td>
                  <td>
                    <span style={{fontWeight:500}}>{t.workflowKey}</span>
                    <span className="mono muted" style={{marginLeft:6,fontSize:12}}>v{t.workflowVersion}</span>
                  </td>
                  <td className="mono" style={{fontSize:12}}>{t.currentAgentKey}</td>
                  <td><StateChip state={t.currentState} /></td>
                  <td className="mono" style={{color:'var(--muted)'}}>{t.roundCount}</td>
                  <td className="muted small">{fmtDate(t.updatedAtUtc)}</td>
                  <td className="actions">
                    {t.currentState === 'Running'
                      ? <Button size="sm" variant="danger">Terminate</Button>
                      : <Button size="sm" variant="ghost" icon={<Ico.trash/>} />}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function TraceDetailPage({ trace, onBack }) {
  return (
    <div className="page">
      <div className="page-header">
        <div>
          <Button size="sm" variant="ghost" icon={<Ico.back/>} onClick={onBack}>Back to traces</Button>
          <h1 style={{marginTop:8}}>Trace</h1>
          <p className="mono muted" style={{fontSize:13, marginTop:4}}>{trace.traceId}</p>
          <div className="trace-header-meta">
            <StateChip state={trace.currentState} />
            <Chip mono>workflow: {trace.workflowKey} v{trace.workflowVersion}</Chip>
            <Chip mono>round: {trace.roundCount}</Chip>
            <Chip mono>current: {trace.currentAgentKey}</Chip>
          </div>
        </div>
        <div className="page-header-actions">
          <Button variant="ghost" icon={<Ico.copy/>}>Copy ID</Button>
          {trace.currentState === 'Running'
            ? <Button variant="danger">Terminate trace</Button>
            : <Button variant="danger" icon={<Ico.trash/>}>Delete trace</Button>}
        </div>
      </div>

      {trace.currentState === 'Failed' && (
        <Card title="Failure">
          <div className="trace-failure">
            <strong>HttpRequestException:</strong> The SSL connection could not be established. Authentication failed because the remote party sent a TLS alert: BadCertificate.
            <div style={{marginTop:6}}>
              <a className="mono-link">Download HTTP diagnostics →</a>
            </div>
          </div>
        </Card>
      )}

      <div style={{display:'grid', gridTemplateColumns:'1fr 360px', gap:16, alignItems:'flex-start'}}>
        <Card title="Execution timeline" flush
          right={<Chip mono>{CF_DATA.TRACE_DETAIL_STEPS.length} hops</Chip>}>
          <div className="timeline">
            {CF_DATA.TRACE_DETAIL_STEPS.map(s => (
              <div key={s.id} className="tl-step" data-state={s.state}>
                <div className="tl-dot">
                  {s.state === 'ok'   && <Ico.check />}
                  {s.state === 'err'  && <Ico.x />}
                  {s.state === 'hitl' && <Ico.hitl style={{width:11,height:11}} />}
                  {s.state === 'run'  && <Ico.play style={{width:9,height:9}} />}
                </div>
                <div className="tl-body">
                  <div className="tl-title">
                    <span className="tl-agent">{s.agentKey}</span>
                    {s.agentVersion && <Chip mono>v{s.agentVersion}</Chip>}
                    {s.port && <Chip variant="accent" mono>{s.port}</Chip>}
                    {s.decision && <span className="muted small">→ {s.decision}</span>}
                  </div>
                  {s.payload && <div className="tl-payload">{s.payload}</div>}
                  {s.logs && <div className="tl-payload">{s.logs.map((l,i) => <div key={i}>• {l}</div>)}</div>}
                </div>
                <div className="tl-when">{s.when}</div>
              </div>
            ))}
          </div>
        </Card>

        <div className="stack">
          <Card title="Decision output">
            <div style={{fontSize:12, color:'var(--muted)', marginBottom:8}}>triage-classifier v12 · port <span className="chip accent mono">p1</span></div>
            <div className="payload-view" style={{maxHeight:220}}>{`{
  "priority": "p1",
  "category": "billing-dispute",
  "confidence": 0.97,
  "reasoning": "Customer quoted Section 4.2 of the MSA and requested legal review. Routing to compliance HITL."
}`}</div>
          </Card>
          <Card title="Context inputs">
            <div className="stack" style={{gap:6}}>
              <div style={{display:'flex',justifyContent:'space-between',fontSize:12}}><span className="mono muted">tenant_id</span><span className="mono">acme-corp</span></div>
              <div style={{display:'flex',justifyContent:'space-between',fontSize:12}}><span className="mono muted">region</span><span className="mono">us-east-1</span></div>
              <div style={{display:'flex',justifyContent:'space-between',fontSize:12}}><span className="mono muted">priority_hint</span><span className="mono">high</span></div>
              <div style={{display:'flex',justifyContent:'space-between',fontSize:12}}><span className="mono muted">source</span><span className="mono">zendesk</span></div>
            </div>
          </Card>
          <Card title="Pinned agent versions">
            <div className="stack" style={{gap:6}}>
              {['intake-router','triage-classifier','pr-composer'].map(k => (
                <div key={k} style={{display:'flex',justifyContent:'space-between',fontSize:12}}>
                  <span className="mono">{k}</span>
                  <Chip mono>v{Math.floor(Math.random()*12)+1}</Chip>
                </div>
              ))}
            </div>
          </Card>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { TracesPage, TraceDetailPage });
