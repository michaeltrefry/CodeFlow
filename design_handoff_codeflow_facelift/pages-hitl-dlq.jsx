// pages-hitl-dlq.jsx — HITL queue + DLQ ops

function HitlPage() {
  const [selected, setSelected] = useState(CF_DATA.HITL_TASKS[0].id);
  const active = CF_DATA.HITL_TASKS.find(t => t.id === selected);

  return (
    <div className="page">
      <PageHeader
        title="HITL queue"
        subtitle="Human-in-the-loop approval inbox. Each item pauses a workflow until decided."
        actions={<>
          <Chip variant="accent" dot>{CF_DATA.HITL_TASKS.filter(t => t.state === 'Pending').length} pending</Chip>
          <Button variant="ghost" icon={<Ico.refresh/>}>Refresh</Button>
        </>}
      />

      <div style={{display:'grid', gridTemplateColumns:'1fr 460px', gap:16, alignItems:'flex-start'}}>
        <div className="hitl-grid">
          {CF_DATA.HITL_TASKS.map(t => (
            <div key={t.id} className="hitl-card" onClick={() => setSelected(t.id)}
              style={{borderColor: t.id === selected ? 'var(--accent)' : undefined, boxShadow: t.id === selected ? '0 0 0 3px var(--accent-weak)' : undefined}}>
              <div className="hitl-ico"><Ico.hitl/></div>
              <div className="hitl-body">
                <div className="hitl-title">
                  <span className="mono">#{t.id}</span>
                  <span>{t.agentKey}</span>
                  <Chip mono>v{t.agentVersion}</Chip>
                  <Chip mono>{t.roundId}</Chip>
                </div>
                <div className="hitl-meta">
                  <span>trace <span className="mono">{t.traceId.slice(0,8)}</span></span>
                  <span>·</span>
                  <span>queued {relTime(t.createdAtUtc)}</span>
                  {t.subflowPath && <><span>·</span><span className="mono">↳ {t.subflowPath.join('/')}</span></>}
                </div>
                <div className="hitl-preview">{t.inputPreview}</div>
              </div>
              <div className="row" style={{flexDirection:'column', alignItems:'flex-end', gap:6}}>
                <Chip variant="warn" dot>Pending</Chip>
                <div className="row">
                  <Button size="sm" variant="danger">Reject</Button>
                  <Button size="sm" variant="primary" icon={<Ico.check/>}>Approve</Button>
                </div>
              </div>
            </div>
          ))}
        </div>

        <div className="stack">
          <Card title="Task detail" right={<Chip mono>#{active.id}</Chip>}>
            <div className="stack">
              <div>
                <div className="field-label">Agent</div>
                <div className="mono" style={{fontSize: 13, marginTop: 2}}>{active.agentKey} <Chip mono style={{marginLeft:4}}>v{active.agentVersion}</Chip></div>
              </div>
              <div>
                <div className="field-label">Trace</div>
                <a className="mono-link" style={{fontSize:13}}>{active.traceId}</a>
              </div>
              <div>
                <div className="field-label">Input preview</div>
                <div className="payload-view" style={{maxHeight:160, marginTop:6}}>{active.inputPreview}</div>
              </div>
              <div>
                <div className="field-label">Decision</div>
                <div className="seg" style={{marginTop:6}}>
                  <button data-active={true}>Approve</button>
                  <button>Request changes</button>
                  <button>Reject</button>
                </div>
              </div>
              <Field label="Comment (attached to decision)">
                <textarea className="textarea" rows={4} placeholder="Document the reasoning — surfaces in the trace decision log."/>
              </Field>
              <div className="row" style={{justifyContent:'flex-end'}}>
                <Button variant="ghost">Cancel</Button>
                <Button variant="primary" icon={<Ico.check/>}>Submit decision</Button>
              </div>
            </div>
          </Card>
        </div>
      </div>
    </div>
  );
}

function DlqPage() {
  const [queue, setQueue] = useState(CF_DATA.DLQ_QUEUES[0].queueName);
  const [selected, setSelected] = useState(CF_DATA.DLQ_MESSAGES[0].messageId);
  const messages = CF_DATA.DLQ_MESSAGES.filter(m => m.queueName === queue);
  const active = CF_DATA.DLQ_MESSAGES.find(m => m.messageId === selected) || messages[0];

  return (
    <div className="page">
      <PageHeader
        title="DLQ ops"
        subtitle="Dead-letter queues from the RabbitMQ message bus. Inspect, fix upstream, retry."
        actions={<>
          <Button variant="ghost" icon={<Ico.refresh/>}>Refresh</Button>
          <Button variant="danger">Purge queue</Button>
        </>}
      />

      <div className="stat-grid">
        {CF_DATA.DLQ_QUEUES.map(q => (
          <div key={q.queueName} className="stat" onClick={() => setQueue(q.queueName)}
            style={{cursor:'default', borderColor: queue === q.queueName ? 'var(--accent)' : undefined}}>
            <div className="stat-value">{q.messageCount}</div>
            <div className="stat-label">{q.queueName.replace('codeflow.','')}</div>
            <div className="stat-delta up"><Ico.alert/> {q.messageCount > 0 ? `${q.messageCount} faulted` : 'clear'}</div>
          </div>
        ))}
      </div>

      <div style={{display:'grid', gridTemplateColumns:'1fr 1.2fr', gap:16, alignItems:'flex-start'}}>
        <div className="card" style={{overflow:'hidden'}}>
          <div className="card-header">
            <h3 className="mono" style={{fontSize:'var(--fs-md)'}}>{queue}</h3>
            <Chip mono>{messages.length} faulted</Chip>
          </div>
          <table className="table">
            <thead><tr><th>Message</th><th>Fault</th><th>Age</th><th></th></tr></thead>
            <tbody>
              {messages.map(m => (
                <tr key={m.messageId} onClick={() => setSelected(m.messageId)}
                  style={{background: selected === m.messageId ? 'var(--surface-2)' : undefined}}>
                  <td><span className="mono" style={{fontSize:11}}>{m.messageId.slice(-12)}</span></td>
                  <td><Chip variant="err" mono>{m.faultExceptionType.replace('Exception','')}</Chip></td>
                  <td className="muted small">{relTime(m.firstFaultedAtUtc)}</td>
                  <td className="actions"><Button size="sm" variant="ghost">Inspect</Button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="stack">
          <Card title="Fault" right={<Chip variant="err" mono>{active.faultExceptionType}</Chip>}>
            <div style={{fontSize:'var(--fs-sm)', color:'var(--text-2)', lineHeight:1.6}}>
              {active.faultExceptionMessage}
            </div>
            <div className="sep"/>
            <div className="stack" style={{gap:4, fontSize:12}}>
              <div className="kv"><span>message id</span><span className="mono small">{active.messageId}</span></div>
              <div className="kv"><span>first fault</span><span className="mono small">{fmtDate(active.firstFaultedAtUtc)}</span></div>
              <div className="kv"><span>input address</span><span className="mono small">{active.originalInputAddress}</span></div>
            </div>
          </Card>
          <Card title="Payload preview"
            right={<div className="row"><Button size="sm" variant="ghost" icon={<Ico.copy/>}>Copy</Button></div>}>
            <div className="payload-view">{active.payloadPreview}</div>
          </Card>
          <div className="row" style={{justifyContent:'flex-end'}}>
            <Button variant="danger" icon={<Ico.trash/>}>Discard</Button>
            <Button variant="primary" icon={<Ico.refresh/>}>Retry message</Button>
          </div>
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { HitlPage, DlqPage });
