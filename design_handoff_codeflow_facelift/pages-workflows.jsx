// pages-workflows.jsx — Workflows list + Canvas editor

const CANVAS_W = 1640;
const CANVAS_H = 700;
const PORT_SPACING = 22;

function nodePortY(node, portIdx, totalPorts) {
  const headH = 38;
  const portsStart = headH + 8;
  return node.y + portsStart + portIdx * PORT_SPACING + 10;
}

function WorkflowsListPage({ onOpenCanvas }) {
  const rows = [
    { key: 'support-triage', name: 'Support triage',  version: 14, nodes: 9, edges: 16, runs: 842, createdAtUtc: '2026-04-22T14:11:00Z' },
    { key: 'pr-review',      name: 'PR review loop',  version: 8,  nodes: 7, edges: 11, runs: 314, createdAtUtc: '2026-04-20T10:30:00Z' },
    { key: 'code-review',    name: 'Code review',     version: 6,  nodes: 6, edges: 9,  runs: 201, createdAtUtc: '2026-04-18T09:22:00Z' },
    { key: 'spec-extract',   name: 'Spec extraction', version: 3,  nodes: 4, edges: 5,  runs: 88,  createdAtUtc: '2026-04-14T16:05:00Z' },
    { key: 'tenant-ops',     name: 'Tenant operations', version: 2,nodes: 5, edges: 7,  runs: 44,  createdAtUtc: '2026-04-11T12:00:00Z' }
  ];
  return (
    <div className="page">
      <PageHeader title="Workflows" subtitle="Versioned graphs of agents, logic, and human checkpoints."
        actions={<Button variant="primary" icon={<Ico.plus/>}>New workflow</Button>} />
      <div className="card" style={{overflow:'hidden'}}>
        <table className="table">
          <thead><tr><th>Key</th><th>Name</th><th>Version</th><th>Nodes</th><th>Edges</th><th>Runs</th><th>Last edit</th><th></th></tr></thead>
          <tbody>
            {rows.map(r => (
              <tr key={r.key} onClick={() => onOpenCanvas(r)}>
                <td className="mono" style={{fontWeight:500}}>{r.key}</td>
                <td>{r.name}</td>
                <td><Chip mono>v{r.version}</Chip></td>
                <td className="mono muted">{r.nodes}</td>
                <td className="mono muted">{r.edges}</td>
                <td className="mono">{r.runs}</td>
                <td className="muted small">{fmtDate(r.createdAtUtc)}</td>
                <td className="actions"><Button size="sm">Edit</Button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function WorkflowCanvasPage({ onBack }) {
  const wf = CF_DATA.WORKFLOW;
  const [selected, setSelected] = useState('n_triage');
  const [drawerOpen, setDrawerOpen] = useState(true);
  const [zoom] = useState(0.82);

  const node = wf.nodes.find(n => n.id === selected);
  const inputsByNode = useMemo(() => {
    const map = {};
    wf.nodes.forEach(n => map[n.id] = []);
    wf.edges.forEach(e => { if (!map[e.to].includes(e.tp)) map[e.to].push(e.tp); });
    return map;
  }, []);

  // compute edge paths
  const edges = wf.edges.map(e => {
    const from = wf.nodes.find(n => n.id === e.from);
    const to   = wf.nodes.find(n => n.id === e.to);
    const fpi = from.outputPorts.indexOf(e.fp);
    const tpi = (inputsByNode[to.id] || []).indexOf(e.tp);
    const x1 = from.x + 220; // width
    const y1 = nodePortY(from, fpi, from.outputPorts.length);
    const x2 = to.x;
    const y2 = nodePortY(to, tpi, inputsByNode[to.id].length);
    const mx = (x1 + x2) / 2;
    const d = `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
    return { ...e, d, x1, y1, x2, y2 };
  });

  const activeEdges = new Set(['n_start→n_intake','n_intake→n_logic','n_logic→n_triage','n_triage→n_hitl']);

  return (
    <div className="page" style={{gap:12}}>
      <div className="page-header">
        <div>
          <Button size="sm" variant="ghost" icon={<Ico.back/>} onClick={onBack}>Workflows</Button>
          <h1 style={{marginTop:8}}>{wf.name}</h1>
          <div className="trace-header-meta">
            <Chip mono>{wf.key}</Chip>
            <Chip mono>v{wf.version}</Chip>
            <Chip>{wf.nodes.length} nodes</Chip>
            <Chip>{wf.edges.length} edges</Chip>
            <Chip variant="accent" dot>unsaved draft</Chip>
          </div>
        </div>
        <div className="page-header-actions">
          <Button variant="ghost">Validate</Button>
          <Button>Discard</Button>
          <Button variant="primary" icon={<Ico.check/>}>Publish v15</Button>
        </div>
      </div>

      <div className="wf-surface">
        <div className="wf-canvas">
          <div className="wf-toolbar">
            <div className="wf-toolbar-group">
              <Button size="sm" variant="ghost" data-active={true}>Select</Button>
              <Button size="sm" variant="ghost">Pan</Button>
              <Button size="sm" variant="ghost">Connect</Button>
            </div>
            <div className="wf-toolbar-group">
              <Button size="sm" variant="ghost" icon={<Ico.plus/>}>Add node</Button>
            </div>
            <div style={{marginLeft:'auto'}} className="wf-toolbar-group">
              <Button size="sm" variant="ghost" data-active={drawerOpen} onClick={() => setDrawerOpen(!drawerOpen)}>{`{ }`} Script</Button>
              <Button size="sm" variant="ghost">Auto-layout</Button>
            </div>
          </div>

          <div className="wf-canvas-inner" style={{transform:`scale(${zoom})`, width: CANVAS_W, height: CANVAS_H}}>
            <svg className="wf-edges" width={CANVAS_W} height={CANVAS_H}>
              {edges.map((e, i) => {
                const isActive = activeEdges.has(`${e.from}→${e.to}`);
                return <Fragment key={i}>
                  <path className={`wf-edge ${isActive ? 'active' : ''}`} d={e.d} />
                  <text className="wf-edge-label" x={(e.x1+e.x2)/2} y={(e.y1+e.y2)/2 - 4} textAnchor="middle">{e.fp}</text>
                </Fragment>;
              })}
            </svg>

            {wf.nodes.map(n => {
              const inputs = inputsByNode[n.id] || [];
              const runState = {n_start:'ok',n_intake:'ok',n_logic:'ok',n_triage:'ok',n_hitl:'run',n_reviewer:'ok',n_compose:null,n_esc:null,n_loop:null}[n.id];
              const runDur  = {n_intake:'820ms',n_logic:'14ms',n_triage:'2.1s',n_hitl:'pending…',n_reviewer:'3.4s'}[n.id];
              return (
                <div key={n.id} className="wf-node"
                  data-kind={n.kind.toLowerCase()}
                  data-selected={selected === n.id}
                  data-state={n.id === 'n_hitl' ? 'active' : (['n_start','n_intake','n_logic','n_triage'].includes(n.id) ? 'active' : null)}
                  style={{left: n.x, top: n.y}}
                  onClick={() => setSelected(n.id)}>
                  <div className="wf-node-head">
                    <span className="wf-node-kind">{n.kind === 'Hitl' ? 'HITL' : n.kind}</span>
                    <span className="wf-node-title">{n.label}</span>
                    {n.hasScript && <span className="wf-node-script-badge">{'{ }'}</span>}
                  </div>
                  <div className="wf-node-body">
                    <div style={{display:'grid', gridTemplateColumns:'1fr 1fr'}}>
                      <div>
                        {inputs.map(p => (
                          <div key={p} className="wf-node-row input">
                            <span className="wf-port filled" />
                            <span className="wf-port-label">{p}</span>
                          </div>
                        ))}
                      </div>
                      <div>
                        {n.outputPorts.map(p => (
                          <div key={p} className="wf-node-row output">
                            <span className="wf-port-label output">{p}</span>
                            <span className="wf-port filled" />
                          </div>
                        ))}
                      </div>
                    </div>
                  </div>
                  {runState && <div className="wf-node-foot">
                    <span className={`run-state ${runState}`}><span className="dot" />{runState === 'run' ? 'running' : runState === 'err' ? 'error' : 'last ok'}</span>
                    <span>{runDur}</span>
                  </div>}
                </div>
              );
            })}
          </div>

          <div className="wf-zoom">
            <Button size="sm" variant="ghost">−</Button>
            <span className="zoom-val">{Math.round(zoom*100)}%</span>
            <Button size="sm" variant="ghost">+</Button>
            <span style={{width:1,height:18,background:'var(--border)',margin:'0 2px'}}></span>
            <Button size="sm" variant="ghost">Fit</Button>
          </div>

          <div className="wf-minimap">
            <div className="wf-minimap-title">Overview</div>
            <div className="wf-minimap-inner">
              {wf.nodes.map(n => (
                <div key={n.id} className="wf-minimap-node" style={{
                  left: `${(n.x / CANVAS_W) * 100}%`,
                  top: `${(n.y / CANVAS_H) * 100}%`,
                  width: `${(220 / CANVAS_W) * 100}%`,
                  height: `${(60 / CANVAS_H) * 100}%`,
                  background: n.id === selected ? 'var(--accent)' : 'var(--accent-dim)'
                }} />
              ))}
              <div className="wf-minimap-view" style={{left:'4%',top:'10%',width:'65%',height:'78%'}} />
            </div>
          </div>

          {drawerOpen && node && node.hasScript && (
            <div className="wf-drawer">
              <div className="wf-drawer-head">
                <h4>routing script · {node.label}</h4>
                <div className="row">
                  <Chip mono>typescript</Chip>
                  <Button size="sm" variant="ghost">Format</Button>
                  <Button size="sm" variant="ghost" icon={<Ico.close/>} onClick={() => setDrawerOpen(false)} />
                </div>
              </div>
              <div className="wf-drawer-body">
                <div className="code-editor">
                  <div className="code-gutter">
                    {Array.from({length: 12}).map((_,i) => <span key={i}>{i+1}</span>)}
                  </div>
<span className="tok-com">// Route by region extracted from the customer profile.</span>{'\n'}
<span className="tok-kw">export function</span> <span className="tok-fn">route</span>(<span className="tok-ident">ctx</span>, <span className="tok-ident">input</span>): <span className="tok-ident">string</span> {'{'}{'\n'}
{'  '}<span className="tok-kw">const</span> <span className="tok-ident">region</span> = (<span className="tok-ident">ctx</span>.<span className="tok-ident">tenant</span>?.<span className="tok-ident">region</span> ?? <span className="tok-str">"us-east-1"</span>).<span className="tok-fn">toLowerCase</span>();{'\n'}
{'  '}<span className="tok-kw">if</span> (<span className="tok-ident">region</span>.<span className="tok-fn">startsWith</span>(<span className="tok-str">"us"</span>)) <span className="tok-kw">return</span> <span className="tok-str">"us"</span>;{'\n'}
{'  '}<span className="tok-kw">if</span> ([<span className="tok-str">"eu"</span>, <span className="tok-str">"uk"</span>].<span className="tok-fn">some</span>(p =&gt; <span className="tok-ident">region</span>.<span className="tok-fn">startsWith</span>(p))) <span className="tok-kw">return</span> <span className="tok-str">"eu"</span>;{'\n'}
{'  '}<span className="tok-kw">return</span> <span className="tok-str">"apac"</span>;{'\n'}
{'}'}
                </div>
              </div>
            </div>
          )}
        </div>

        <aside className="wf-side">
          <div className="wf-side-head">
            <h3>{node?.label || 'Inspector'}</h3>
            <Chip mono variant="accent">{node?.kind}</Chip>
          </div>
          <div className="wf-side-body">
            {node?.kind === 'Agent' && <>
              <Field label="Agent key"><input className="input mono" defaultValue={node.agentKey} /></Field>
              <Field label="Version"><input className="input mono" defaultValue={`v${node.agentVersion}`} /></Field>
              <Field label="Pin to version" hint="Leave unpinned to always use latest"><input className="input mono" defaultValue="latest" /></Field>
            </>}
            {node?.kind === 'Logic' && <>
              <Field label="Node label"><input className="input" defaultValue={node.label} /></Field>
              <Field label="Routing script" hint="Run the script editor in the code drawer below.">
                <Button size="sm" onClick={() => setDrawerOpen(true)}>{'{ } Edit script'}</Button>
              </Field>
            </>}
            {node?.kind === 'ReviewLoop' && <>
              <Field label="Subflow workflow key"><input className="input mono" defaultValue="pr-review" /></Field>
              <Field label="Max rounds"><input className="input mono" type="number" defaultValue={node.reviewMaxRounds} /></Field>
              <Field label="Loop on decision"><select className="select" defaultValue={node.loopDecision}><option>Rejected</option><option>ChangesRequested</option></select></Field>
            </>}

            <div>
              <div style={{fontSize:11, textTransform:'uppercase', letterSpacing:'.08em', color:'var(--faint)', fontWeight:600, margin:'4px 0 8px'}}>Output ports</div>
              <div className="stack" style={{gap:6}}>
                {(node?.outputPorts || []).map(p => (
                  <div key={p} style={{display:'flex',alignItems:'center',justifyContent:'space-between',padding:'7px 10px', background:'var(--surface-2)', border:'1px solid var(--border)', borderRadius:'var(--radius)'}}>
                    <div style={{display:'flex',alignItems:'center',gap:8}}>
                      <span className="wf-port filled" style={{'--_kind':'var(--sem-blue)'}} />
                      <span className="mono" style={{fontSize:12}}>{p}</span>
                    </div>
                    <Button size="sm" variant="ghost" icon={<Ico.x/>} />
                  </div>
                ))}
                <Button size="sm" variant="ghost" icon={<Ico.plus/>}>Add port</Button>
              </div>
            </div>

            <div className="sep"/>
            <div style={{fontSize:11, textTransform:'uppercase', letterSpacing:'.08em', color:'var(--faint)', fontWeight:600}}>Last run</div>
            <div className="stack" style={{gap:4, fontSize:12}}>
              <div style={{display:'flex',justifyContent:'space-between'}}><span className="muted">State</span><Chip variant="ok" dot>ok</Chip></div>
              <div style={{display:'flex',justifyContent:'space-between'}}><span className="muted">Duration</span><span className="mono">2.1s</span></div>
              <div style={{display:'flex',justifyContent:'space-between'}}><span className="muted">Tokens</span><span className="mono">1,842 in · 412 out</span></div>
              <div style={{display:'flex',justifyContent:'space-between'}}><span className="muted">Cost</span><span className="mono">$0.014</span></div>
            </div>
          </div>
        </aside>
      </div>
    </div>
  );
}

Object.assign(window, { WorkflowsListPage, WorkflowCanvasPage });
