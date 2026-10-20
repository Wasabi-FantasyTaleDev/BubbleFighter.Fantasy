/** Advance the cn-279 narrator once per rendered frame, never in a catch-up burst. */
export function narratorState(text, interval, now=0) {
  if(typeof text!=='string'||!Number.isFinite(interval)||interval<=0)throw Error('Invalid narrator timing');
  return {text,interval,visible:0,deadline:now+interval,phase:'typing',finishedAt:undefined};
}

/** cn-279 sub_785010 increments one UTF-16 unit and then restarts its timer. */
export function narratorFrame(state, now) {
  if(state.phase!=='typing'||now<state.deadline)return 'idle';
  if(state.visible<state.text.length){state.visible++;state.deadline=now+state.interval;return 'character';}
  state.phase='complete';state.finishedAt=now;return 'complete';
}

/** cn-279 sub_7849D0/sub_785010: first click reveals, the next click advances. */
export function narratorClick(state, now) {
  if(state.phase==='typing'){state.visible=state.text.length;state.phase='complete';state.finishedAt=now;return 'reveal';}
  return 'advance';
}

/** cn-279 sub_53FDF0/sub_540180 measure each explicit line before vertical alignment. */
export function narratorLines(text, glyphs, size=15) {
  if(!text)return {height:0,lines:[]};
  const paragraphs=text.split('\n');
  if(text.endsWith('\n'))paragraphs.pop();
  let total=0;
  const lines=paragraphs.map((line,index)=>{
    const characters=line+(index<paragraphs.length-1||text.endsWith('\n')?'\n':'');
    const height=Math.max(...Array.from(characters,c=>glyphs[c]?.lineHeight??size),size);
    const result={text:line,y:total,height};total+=height;return result;
  });
  return {height:total,lines};
}

/** Presentation mode skips gameplay-only nodes and wraps after the last dialogue. */
export function nextNarratorPage(pages, page, loop=false) {
  if(!page)return undefined;
  const stagePages=pages.filter(candidate=>candidate.stage===page.stage);
  const native=stagePages.find(candidate=>String(candidate.progress)===page.nextProgress);
  if(native)return native;
  if(loop&&stagePages.length)return stagePages[(stagePages.findIndex(candidate=>candidate.progress===page.progress)+1)%stagePages.length];
  return undefined;
}
