'use strict';
// Build 205529 only. Reads the lyric parsers already owned by NetEase.
// The native GDI+ outline renderer receives a two-line secondary text block.
const image = Process.getModuleByName('cloudmusic.dll');
const vectorOf = new NativeFunction(image.base.add(0x11301d0), 'pointer', ['pointer']);
let pair = null, errors = 0, fontRatio = 1, announced = false;
const user32=Process.getModuleByName('user32.dll');
const findWindow=new NativeFunction(user32.getExportByName('FindWindowW'),'pointer',['pointer','pointer']);
const getRect=new NativeFunction(user32.getExportByName('GetWindowRect'),'bool',['pointer','pointer']);
const setWindowPos=new NativeFunction(user32.getExportByName('SetWindowPos'),'bool',['pointer','pointer','int','int','int','int','uint']);
const getDpi=new NativeFunction(user32.getExportByName('GetDpiForWindow'),'uint',['pointer']);
const monitorFromWindow=new NativeFunction(user32.getExportByName('MonitorFromWindow'),'pointer',['pointer','uint']);
const getMonitorInfo=new NativeFunction(user32.getExportByName('GetMonitorInfoW'),'bool',['pointer','pointer']);
const desktopClass=Memory.allocUtf16String('DesktopLyrics');
let enlarged=ptr(0), resizeErrors=0;
function enlargeDesktopWindow() {
  try {
    const hwnd=findWindow(desktopClass,ptr(0));
    if(hwnd.isNull()){enlarged=ptr(0);return;}
    // Expand each new lyric window once. After that, preserve manual resizing.
    if(hwnd.equals(enlarged))return;
    const rect=Memory.alloc(16);
    if(!getRect(hwnd,rect))return;
    const x=rect.readS32(),y=rect.add(4).readS32();
    const width=rect.add(8).readS32()-x,height=rect.add(12).readS32()-y;
    if(width<300||height<80)return;
    const dpi=getDpi(hwnd)||96;
    const work=Memory.alloc(40);work.writeU32(40);
    const monitor=monitorFromWindow(hwnd,2);
    if(monitor.isNull()||!getMonitorInfo(monitor,work))return;
    const workTop=work.add(24).readS32(),workBottom=work.add(32).readS32();
    const targetHeight=Math.min(Math.round(320*dpi/96),workBottom-workTop-16);
    fontRatio=Math.min(1,Math.round(138*dpi/96)/targetHeight);
    const targetY=Math.max(workTop+8,Math.min(y,workBottom-targetHeight-8));
    if(Math.abs(height-targetHeight)<2&&y===targetY){enlarged=hwnd;return;}
    if(setWindowPos(hwnd,ptr(0),x,targetY,width,targetHeight,0x14)){
      send({type:'window-resized',width,height:targetHeight,dpi});
      enlarged=hwnd;
    }
  } catch(e) {
    if(resizeErrors++<3)send({type:'resize-error',message:String(e)});
  }
}
enlargeDesktopWindow();
setInterval(enlargeDesktopWindow,3000);
function inLyricRenderer(context) {
  return Thread.backtrace(context,Backtracer.ACCURATE).some(p=>{
    const r=p.sub(image.base).toInt32();
    return r>=0x1122ce0&&r<0x11231cf;
  });
}
// The client scales both fonts when the native lyric window grows. Change the
// font creation input and path outline size while keeping the larger canvas.
Interceptor.attach(image.base.add(0x110ac3b),{onEnter(){
  if(fontRatio>=0.99||!inLyricRenderer(this.context))return;
  const spot=this.context.rsp.add(0x18),size=spot.readFloat();
  if(size>10&&size<400){
    spot.writeFloat(size*fontRatio);
  }
}});
function linesIn(vector) {
  const begin=vector.readPointer(), end=vector.add(8).readPointer();
  const count=end.sub(begin).toInt32()/64;
  if(!Number.isInteger(count)||count<1||count>10000)return null;
  return {begin,count};
}
function lineOf(lines,index) {
  if(!lines||index<0||index>=lines.count)return null;
  const entry=lines.begin.add(index*64);
  const start=entry.readS32(),duration=entry.add(4).readS32();
  const value=entry.add(0x20);
  const len=value.add(0x10).readU64().toNumber(),cap=value.add(0x18).readU64().toNumber();
  if(len<1||len>1000||cap<len||cap>100000)return null;
  return {start,duration,text:(cap>7?value.readPointer():value).readUtf16String(len),index};
}
function indexAt(lines,time) {
  let lo=0,hi=lines.count;
  while(lo<hi){const mid=(lo+hi)>>>1;if(lines.begin.add(mid*64).readS32()<=time)lo=mid+1;else hi=mid;}
  return lo-1;
}
function nearestLine(lines,start,maxSkew) {
  if(!lines)return null;
  const at=indexAt(lines,start);
  const before=lineOf(lines,at),after=lineOf(lines,at+1);
  const best=!before?after:!after?before:
    Math.abs(before.start-start)<=Math.abs(after.start-start)?before:after;
  return best&&Math.abs(best.start-start)<=maxSkew?best:null;
}
function alignTrackForFrame(track,original,restore) {
  if(!track||!original||track.count>500||original.count>500)return;
  const changes=[];
  for(let i=0;i<track.count;i++){
    const entry=track.begin.add(i*64),start=entry.readS32();
    const match=nearestLine(original,start,3000);
    changes.push({entry,start,duration:entry.add(4).readS32(),target:match?match.start:start});
  }
  // Binary search in the native renderer requires ordered timestamps.
  for(let i=1;i<changes.length;i++){
    if(changes[i].target<changes[i-1].target)return;
  }
  for(let i=0;i<changes.length;i++){
    const change=changes[i];
    const duration=i+1<changes.length?
      Math.max(0,changes[i+1].target-change.target):change.duration;
    if(change.target===change.start&&duration===change.duration)continue;
    restore.push(change);
    change.entry.writeS32(change.target);
    change.entry.add(4).writeS32(duration);
  }
}
Interceptor.attach(image.base.add(0x111a5c0), {
  onEnter(args) {
    this.restoreTimes=[];
    try {
      const context=args[0].add(8).readPointer();
      const parser=context.add(0x10).readPointer(),manager=parser.readPointer();
      const list=manager.add(0x18).readPointer();
      if(manager.add(0x20).readPointer().sub(list).toInt32()!==16){pair=null;return;}
      const time=args[1].toInt32();
      const originalLines=linesIn(args[0].add(0x10).readPointer());
      const secondaryLines=linesIn(args[0].add(0x18).readPointer());
      const translationLines=linesIn(vectorOf(list.readPointer()));
      const romanizationLines=linesIn(vectorOf(list.add(8).readPointer()));
      if(!originalLines||!secondaryLines||!translationLines||!romanizationLines){pair=null;return;}
      // 111a5c0 selects from producer+0x18 for the desktop secondary row.
      // The parser vectors below are source data for pairing and must stay intact.
      alignTrackForFrame(secondaryLines,originalLines,this.restoreTimes);
      // NetEase caches the secondary text after drawing it. If that track
      // advances before the Japanese track, rewriting it from the Japanese
      // playback position leaves an old two-line block on screen. Instead,
      // pair each native secondary line with its counterpart at draw time.
      const replacements=new Map(),distance=new Map();
      for(const [track,other,isTranslation] of [
        [translationLines,romanizationLines,true],
        [romanizationLines,translationLines,false]]){
        const at=indexAt(track,time);
        for(let i=Math.max(0,at-5);i<=Math.min(track.count-1,at+5);i++){
          const line=lineOf(track,i);
          if(!line||!line.text.trim())continue;
          const before=lineOf(track,i-1),after=lineOf(track,i+1);
          const gap=Math.min(before?line.start-before.start:Infinity,
                             after?after.start-line.start:Infinity);
          const skew=Math.min(2000,Math.max(500,Number.isFinite(gap)?gap*0.45:2000));
          const companion=nearestLine(other,line.start,skew);
          if(!companion||!companion.text.trim())continue;
          const trans=isTranslation?line:companion;
          const roma=isTranslation?companion:line;
          if(trans.text===roma.text)continue;
          const rank=Math.abs(line.start-time);
          if(!distance.has(line.text)||rank<distance.get(line.text)){
            replacements.set(line.text,roma.text+'\n'+trans.text);
            distance.set(line.text,rank);
          }
        }
      }
      pair=replacements.size?{replacements}:null;
    } catch(e) { pair=null;if(errors++<3)send({type:'error',message:String(e)}); }
  },
  onLeave() {
    for(const change of this.restoreTimes||[]){
      change.entry.writeS32(change.start);
      change.entry.add(4).writeS32(change.duration);
    }
  }
});
for (const [dll,fn] of [['gdiplus.dll','GdipAddPathString'],['gdiplus.dll','GdipMeasureString'],['gdi32.dll','GetTextExtentPoint32W']]) {
Interceptor.attach(Process.getModuleByName(dll).getExportByName(fn),{
  onEnter(args){
    this.replacement=null;
    if(!pair)return;
    try{
      const len=args[2].toInt32();
      if((len<1&&len!==-1)||len>1000)return;
      const text=len===-1?args[1].readUtf16String():args[1].readUtf16String(len);
      const replacement=pair.replacements.get(text);
      if(replacement==null||replacement===text)return;
      this.replacement=Memory.allocUtf16String(replacement);
      args[1]=this.replacement;args[2]=ptr(replacement.length);
      if(fn==='GdipAddPathString'&&!announced){announced=true;send({type:'triline-rendered'});}
    }catch(e){if(errors++<3)send({type:'error',message:String(e)});}
  },
  onLeave(){this.replacement=null;}
});
}
send({type:'ready',pid:Process.id});
