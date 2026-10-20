<script setup lang="ts">
import {computed,onBeforeUnmount,ref,watch} from 'vue';
import {narratorClick,narratorFrame,narratorLines,narratorState,nextNarratorPage} from './tutorial-model.js';

export interface BfTutorialPage {stage:string;progress:number;text:string;balloon:string;clickComplete:boolean;holdMs:number;nextProgress:string;sourceProgress?:number;sourceText?:string;part?:number;parts?:number}
interface Glyph {x:number;y:number;width:number;height:number;left:number;top:number;advance:number;lineHeight:number}
interface TutorialManifest {textPrintSpeed:number;dialogues:BfTutorialPage[]}
const props=withDefaults(defineProps<{
  stage?:string;progress?:number;text?:string;resourceRoot?:string;
  audioContext?:AudioContext|null;character?:'Dizni'|'Dao';sound?:boolean;volume?:number;autoplay?:boolean;loop?:boolean;advanceLabel?:string;revealLabel?:string;
}>(),{stage:'FantasySetup',progress:1,resourceRoot:'./tutorial/fantasy-setup/zh-Hans/',character:'Dizni',sound:true,volume:1,autoplay:false,loop:false,advanceLabel:'Continue dialogue',revealLabel:'Show full dialogue'});
const emit=defineEmits<{ready:[BfTutorialPage[]];complete:[];advance:[BfTutorialPage|undefined];error:[Error]}>();
const canvas=ref<HTMLCanvasElement>(),status=ref('loading'),visible=ref('');
const root=computed(()=>props.resourceRoot.replace(/\/?$/,'/'));
const localImage=(name:string)=>`${root.value}Images/TutorialStage/${name}`;
let manifest:TutorialManifest|undefined,glyphs:Record<string,Glyph>={},atlas:HTMLImageElement|undefined;
let state:ReturnType<typeof narratorState>|undefined,page:BfTutorialPage|undefined,frame=0,revision=0,loadRevision=0,disposed=false;
let audio:AudioContext|undefined,buffers:Record<string,AudioBuffer>={};
let gain:GainNode|undefined;
const sources=new Set<AudioBufferSourceNode>();

async function responseJson(path:string){const response=await fetch(root.value+path);if(!response.ok)throw Error(`Tutorial resource ${path}: ${response.status}`);return response.json();}
async function load(){
  const current=++loadRevision;
  try{
    const [data,font]=await Promise.all([responseJson('manifest.json'),responseJson('glyphs.json')]);
    if(disposed||current!==loadRevision)return;
    const bitmap=new Image();bitmap.src=root.value+'glyphs.png';
    // Reveal the group only when both the glyph atlas and visible artwork can paint together.
    await Promise.all([bitmap.decode(), ...[props.character+'.png','ChatBalloonNew.png'].map(async name=>{
      const artwork=new Image();artwork.src=localImage(name);await artwork.decode();
    })]);
    if(disposed||current!==loadRevision)return;
    manifest=data;glyphs=font.glyphs;atlas=bitmap;
    if(props.sound){audio??=props.audioContext??new AudioContext();if(audio.state==='running')await unlockSound();}
    if(disposed||current!==loadRevision)return;
    status.value='ready';emit('ready',data.dialogues);restart();
  }catch(reason){if(!disposed&&current===loadRevision){status.value='error';emit('error',reason instanceof Error?reason:new Error(String(reason)));}}
}

function draw(){
  const context=canvas.value?.getContext('2d');if(!context||!state||!atlas)return;
  context.clearRect(0,0,250,100);context.imageSmoothingEnabled=false;
  const text=state.text.slice(0,state.visible);visible.value=text;
  const layout=narratorLines(text,glyphs,15),top=50-Math.floor(layout.height/2);
  for(const line of layout.lines){
    let x=0;
    for(const character of line.text){
      const g=glyphs[character];
      if(g){if(g.width&&g.height)context.drawImage(atlas,g.x,g.y,g.width,g.height,x+g.left,top+line.y+14-g.top,g.width,g.height);x+=g.advance;}
      else {
        // Arbitrary host text outside the prepared tutorial atlas uses the original TTF.
        context.font='400 15px BF';context.fillStyle='#222222';context.textBaseline='alphabetic';
        context.fillText(character,x,top+line.y+14);x+=Math.round(context.measureText(character).width);
      }
    }
  }
}

function play(name:string){
  if(!props.sound||audio?.state!=='running'||!buffers[name])return;
  // A bounded set also covers hosts that supply a longer replacement sample.
  if(sources.size>=8){const oldest=sources.values().next().value;oldest?.stop();if(oldest)sources.delete(oldest);}
  if(!gain){gain=audio.createGain();gain.connect(audio.destination);}
  gain.gain.value=Math.max(0,Math.min(1,props.volume));
  const source=audio.createBufferSource();source.buffer=buffers[name];source.connect(gain);sources.add(source);
  source.onended=()=>{sources.delete(source);source.disconnect();};source.start();
}

async function unlockSound(){
  if(!props.sound)return;
  audio??=props.audioContext??new AudioContext();await audio.resume();
  if(!buffers.type){
    const context=audio;
    const entries=await Promise.all(['type','textbox'].map(async name=>{
      const response=await fetch(`${root.value}Sounds/ui-tut-${name}.wav`);
      if(!response.ok)throw Error(`Tutorial sound ${name}: ${response.status}`);
      return [name,await context.decodeAudioData(await response.arrayBuffer())] as const;
    }));
    if(disposed)return;buffers=Object.fromEntries(entries);
  }
}

function tick(now:number){
  if(disposed||!state)return;
  const result=narratorFrame(state,now);
  if(result==='character'){draw();play('type');}
  if(result==='complete')emit('complete');
  if(props.autoplay&&state.phase==='complete'&&page&&page.holdMs>=0&&now-(state.finishedAt??now)>=page.holdMs){status.value='complete';emit('advance',nextPage());return;}
  frame=requestAnimationFrame(tick);
}

function nextPage(){return nextNarratorPage(manifest?.dialogues??[],page,props.loop);}
function imageError(){emit('error',Error('Tutorial image could not be loaded'));}
function restart(){
  if(!manifest||!canvas.value)return;
  cancelAnimationFrame(frame);revision++;
  page=manifest.dialogues.find(candidate=>candidate.stage===props.stage&&candidate.progress===props.progress);
  if(!page&&props.text===undefined){emit('error',Error(`Missing tutorial page ${props.stage}/${props.progress}`));return;}
  state=narratorState(props.text??page!.text,manifest.textPrintSpeed,performance.now());
  draw();
  // Narration remains readable when browser audio policy requires another gesture.
  status.value='playing';play('textbox');frame=requestAnimationFrame(tick);
}
async function start(){try{await unlockSound();if(!disposed)restart();}catch(reason){emit('error',reason instanceof Error?reason:new Error(String(reason)));}}
function reveal(){
  if(!state||state.phase!=='typing')return;
  if(narratorClick(state,performance.now())==='reveal'){draw();play('type');emit('complete');}
}
async function click(){
  const current=revision;
  try{await unlockSound();if(disposed||current!==revision||!state||page?.clickComplete===false)return;
    const result=narratorClick(state,performance.now());
    if(result==='reveal'){draw();play('type');emit('complete');}
    else emit('advance',nextPage());
  }catch(reason){emit('error',reason instanceof Error?reason:new Error(String(reason)));}
}
watch([canvas,()=>props.stage,()=>props.progress,()=>props.text,()=>props.character],()=>restart(),{flush:'post'});
watch(()=>props.sound,()=>restart());
watch(root,()=>{status.value='loading';cancelAnimationFrame(frame);manifest=undefined;state=undefined;atlas=undefined;visible.value='';buffers={};for(const source of sources)source.stop();sources.clear();void load();},{immediate:true});
onBeforeUnmount(()=>{disposed=true;cancelAnimationFrame(frame);for(const source of sources)source.stop();sources.clear();gain?.disconnect();if(audio&&audio!==props.audioContext)void audio.close();});
defineExpose({start,restart,status});
</script>

<template>
  <div class="bf-tutorial-dialogue" :data-status="status" :data-progress="progress" :data-visible-count="state?.visible??0" @click="click">
    <img class="bf-tutorial-portrait" :src="localImage(character+'.png')" :alt="character" draggable="false" @error="imageError"/>
    <button class="bf-tutorial-portrait-hit" :aria-label="revealLabel" type="button" @click.stop="reveal" />
    <img class="bf-tutorial-balloon" :src="localImage('ChatBalloonNew.png')" alt="" draggable="false" @error="imageError"/>
    <button class="bf-tutorial-balloon-hit" :aria-label="revealLabel" type="button" @click.stop="reveal" />
    <canvas ref="canvas" class="bf-tutorial-copy" width="250" height="100" role="img" :aria-label="visible"/>
    <button class="bf-tutorial-skip" :aria-label="advanceLabel" type="button"/>
  </div>
</template>

<style>
.bf-tutorial-dialogue{position:relative;width:1024px;height:768px;isolation:isolate;cursor:default;pointer-events:none}
.bf-tutorial-dialogue img{position:absolute;max-width:none;pointer-events:none;user-select:none}
.bf-tutorial-portrait{left:748px;top:524.5px;width:272px;height:243px}
.bf-tutorial-portrait-hit{position:absolute;left:748px;top:524.5px;width:272px;height:243px;pointer-events:auto;cursor:pointer;border:0;background:transparent;padding:0}
.bf-tutorial-portrait-hit:focus-visible{outline:2px solid #123a56;outline-offset:-4px;border-radius:24px}
.bf-tutorial-balloon{left:683px;top:378px;width:328px;height:192px}
.bf-tutorial-balloon-hit{position:absolute;left:683px;top:378px;width:328px;height:192px;pointer-events:auto;cursor:pointer;border:0;background:transparent;padding:0;border-radius:24px}
.bf-tutorial-balloon-hit:focus-visible{outline:2px solid #123a56;outline-offset:-4px}
.bf-tutorial-copy{position:absolute;left:727px;top:400px;width:250px;height:100px;pointer-events:none}
.bf-tutorial-skip{position:absolute;left:925px;top:526px;width:72px;height:38px;cursor:pointer;pointer-events:auto;border:0;background:transparent;padding:0}
.bf-tutorial-skip:focus-visible{outline:2px solid #fff;border-radius:5px}
.bf-tutorial-start{position:absolute;left:715px;top:427px;width:260px;pointer-events:auto;padding:9px 5px;border:1px solid #8193a0;border-radius:5px;background:#f5faff;color:#23435d;font:15px system-ui;cursor:pointer}
</style>
