#!/usr/bin/env python3
import json, os, pathlib, re, subprocess, time, statistics

ROOT=pathlib.Path.cwd(); CODEMAP=os.environ.get("CODEMAP","dotnet src/CodeMap.Cli/bin/Release/net10.0/codemap.dll")
TARGET="CodeMap.slnx"; RESULT=ROOT/"artifacts"/"quota-ab-results.json"
HARNESS_VERSION="quota-ab-1.1-v4v5"
TASKS=[("find","IncrementalCodeMapIndexer"),("find","CodeMapQueryService"),("find","RiskScorer"),("callers","RunContextAsync"),("callers","RunFlowAsync"),("callers","LoadQueryServiceAsync"),("callees","RunContextAsync"),("callees","RunChangedImpactAsync"),("callees","RunFlowAsync"),("impact","BuildMap"),("impact","ResolveSymbol"),("impact","ImplementationRelations")]
SOURCE_EXT=(".cs",".razor",".xaml",".js",".ts")

def cmd(parts):
    t=time.perf_counter(); p=subprocess.run(parts,cwd=ROOT,text=True,encoding="utf-8",errors="replace",stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    return p.returncode,p.stdout,p.stderr,time.perf_counter()-t

def ca(*a): return CODEMAP.split()+list(a) if CODEMAP.startswith("dotnet ") else [CODEMAP,*a]

def sz(s):
    b=len(s.encode("utf-8",errors="ignore")); return {"bytes":b,"tokens_est":round(b/4.0,1),"chars":len(s)}

def git_files():
    rc,out,_,_=cmd(["git","ls-files"]); return out.splitlines() if rc==0 else []
TRACKED=git_files(); SOURCES=[p for p in TRACKED if p.lower().endswith(SOURCE_EXT)]
PROJECT_ROOTS={pathlib.PurePosixPath(p).stem:str(pathlib.PurePosixPath(p).parent) for p in TRACKED if p.endswith(".csproj")}

def resolve(raw, project=None):
    raw=raw.replace("\\","/").lstrip("./")
    if raw in SOURCES: return raw
    if project and project in PROJECT_ROOTS:
        candidate=str(pathlib.PurePosixPath(PROJECT_ROOTS[project])/raw)
        if candidate in SOURCES: return candidate
    suffix="/"+raw
    matches=[p for p in SOURCES if p.endswith(suffix)]
    return matches[0] if len(matches)==1 else None

def json_locations(text):
    try: obj=json.loads(text)
    except Exception: return []
    locs=[]
    def walk(x):
        if isinstance(x,dict):
            f=x.get("file")
            if isinstance(f,str) and f.lower().endswith(SOURCE_EXT):
                line=x.get("startLine") or x.get("line") or 1
                try: line=int(line)
                except Exception: line=1
                locs.append((f,line,x.get("project")))
            for v in x.values(): walk(v)
        elif isinstance(x,list):
            for v in x: walk(v)
    walk(obj); return locs

def snippets(locs,max_files=5,radius=40):
    chunks=[]; used=[]
    for item in locs:
        raw,line=(item[0],item[1]); project=item[2] if len(item)>2 else None
        rel=resolve(raw,project)
        if not rel or rel in used: continue
        p=ROOT/rel
        try: lines=p.read_text(errors="ignore").splitlines()
        except Exception: continue
        line=max(1,line); lo=max(0,line-1-radius); hi=min(len(lines),line+radius)
        chunks.append(f"--- {rel}:{line} ---\n"+"\n".join(lines[lo:hi])); used.append(rel)
        if len(used)>=max_files: break
    return "\n".join(chunks),used

def baseline(mode,q):
    grep=["git","grep","-n","-F",q,"--","*.cs","*.razor","*.xaml","*.js","*.ts"]
    rc,out,err,elapsed=cmd(grep); locs=[]
    for line in out.splitlines()[:40]:
        m=re.match(r"^([^:]+):(\d+):(.*)$",line)
        if m: locs.append((m.group(1),int(m.group(2)),None))
    sn,used=snippets(locs); payload="\n".join(out.splitlines()[:40])+("\n"+sn if sn else "")
    return {"rc":rc,"elapsed_s":elapsed,"tool_calls":1+len(used),"files":used,**sz(payload)}

def cmap(mode,q,evidence=False):
    outputs=[]; locs=[]; elapsed=0.0; calls=0
    r1,o1,e1,t1=cmd(ca("find",q,"--json","--max-results","20")); outputs.append(o1 or e1); locs+=json_locations(o1); elapsed+=t1; calls+=1; final=r1
    if mode!="find" and r1==0:
        args=[mode,q,"--json","--max-results","20"]
        if mode in ("callees","impact"): args += ["--depth","2"]
        if evidence: args += ["--evidence"]
        r2,o2,e2,t2=cmd(ca(*args)); outputs.append(o2 or e2); locs+=json_locations(o2); elapsed+=t2; calls+=1; final=r2
    sn,used=snippets(locs); calls+=len(used); payload="\n".join(outputs)+("\n"+sn if sn else "")
    return {"rc":final,"elapsed_s":elapsed,"tool_calls":calls,"files":used,**sz(payload)}

def effective(c,b):
    e=dict(c)
    if c["rc"]!=0:
        for k in ("bytes","tokens_est","tool_calls","elapsed_s"): e[k]+=b[k]
        e["files"]=sorted(set(e["files"]+b["files"]))
    return e

def aggregate(rows,side):
    return {k:sum(r[side][k] for r in rows) for k in ("bytes","tokens_est","tool_calls","elapsed_s")}

def savings(base,x): return {k:round((1-x[k]/max(1,base[k]))*100,2) for k in ("bytes","tokens_est","tool_calls")}

def main():
    RESULT.parent.mkdir(parents=True,exist_ok=True)
    rc,out,err,index_s=cmd(ca("index",TARGET,"--force"))
    if rc!=0: raise SystemExit(f"index failed: {err or out}")
    noop=[]; noop_failures=[]
    for _ in range(5):
        r,o,e,t=cmd(ca("update",TARGET))
        if r==0: noop.append(t)
        else: noop_failures.append(e or o)
    if not noop: raise SystemExit(f"no-op update failed on all 5 attempts: {noop_failures[-1] if noop_failures else 'no output'}")
    rows=[]
    for mode,q in TASKS:
        b=baseline(mode,q); lean=cmap(mode,q,False); verified=cmap(mode,q,True)
        le=effective(lean,b); ve=effective(verified,b); bs=set(b["files"])
        rows.append({"mode":mode,"query":q,"baseline":b,"lean":lean,"verified":verified,"effective_lean":le,"effective_verified":ve,
                     "lean_fallback":lean["rc"]!=0,"verified_fallback":verified["rc"]!=0,
                     "lean_file_overlap":round(len(bs&set(lean["files"]))/max(1,len(bs)),3),"verified_file_overlap":round(len(bs&set(verified["files"]))/max(1,len(bs)),3)})
    base=aggregate(rows,"baseline"); lean=aggregate(rows,"effective_lean"); ver=aggregate(rows,"effective_verified")
    summary={"tasks":len(rows),"index_seconds":index_s,"noop_update_seconds_median":statistics.median(noop),"noop_update_failures":len(noop_failures),
             "baseline":base,"lean":lean,"verified":ver,"lean_savings_pct":savings(base,lean),"verified_savings_pct":savings(base,ver),
             "lean_fallbacks":sum(r["lean_fallback"] for r in rows),"verified_fallbacks":sum(r["verified_fallback"] for r in rows),
             "median_lean_file_overlap":statistics.median(r["lean_file_overlap"] for r in rows),"median_verified_file_overlap":statistics.median(r["verified_file_overlap"] for r in rows)}
    result={"harness_version":HARNESS_VERSION,"methodology":{"token_proxy":"UTF-8 bytes / 4","json_schema":"CLI --json output; v4 without --evidence, v5 with --evidence (field names file/startLine/project unchanged from historical v2/v3 contracts)","baseline":"git grep top 40 hits + up to 5 source snippets around hit ±40 lines","lean":"find JSON + one relation JSON + source snippets around project-qualified match locations","verified":"find JSON + relation --evidence JSON + up to 5 source snippets around project-qualified match / uniquely resolved evidence locations","fallback":"failed CodeMap attempt plus full baseline cost","note":"file overlap is diagnostic only; semantic definition selection may intentionally differ from grep's first files"},"summary":summary,"rows":rows}
    RESULT.write_text(json.dumps(result,indent=2),encoding="utf-8"); print(json.dumps(summary,indent=2))
if __name__=="__main__": main()
