// Authoring tool; product validation uses the committed, embedded files offline.
import fs from 'node:fs';
const dir = new URL('../docs/schemas/', import.meta.url);
fs.mkdirSync(dir, {recursive:true});
const ref = name => ({$ref:`${name}.schema.json`});
const def = name => ({$ref:`common.schema.json#/$defs/${name}`});
const str = {type:'string', minLength:1, maxLength:4096};
const id = {type:'string', pattern:'^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$'};
const integer = (min=0,max=9007199254740991)=>({type:'integer',minimum:min,maximum:max});
const arr = (items,min=0,max=1000)=>({type:'array',items,minItems:min,maxItems:max});
const obj = (properties,required=Object.keys(properties))=>({type:'object',additionalProperties:false,properties,required});
const header = kind=>({kind:{const:kind},schemaVersion:{const:1}});
function save(name,schema){fs.writeFileSync(new URL(`${name}.schema.json`,dir),JSON.stringify({$schema:'https://json-schema.org/draft/2020-12/schema',$id:`https://gua-playtest.dev/schema/${name}.schema.json`,...schema},null,2)+'\n');}
const gua=name=>({$ref:`https://gua.dev/schema/${name}.schema.json`});
const uiTarget=obj({source:{const:'ui'},selector:gua('selector')});
const objectTarget=obj({source:{const:'object'},selector:gua('world-selector')});
const worldTarget=obj({source:{const:'world'}});
const target={oneOf:[uiTarget,objectTarget,worldTarget]};
const valueType={oneOf:[obj({type:{enum:['bool','integer','number','string']}}),obj({type:{const:'enum'},enumType:str}),obj({type:{enum:['list','set']},elementType:{enum:['bool','integer','number','string']}}),obj({type:{enum:['list','set']},elementType:{const:'enum'},enumType:str})]};
const read={oneOf:[
  obj({target:{oneOf:[uiTarget,objectTarget]},region:{const:'standard'},field:str,valueType}),
  obj({target:{oneOf:[uiTarget,objectTarget]},region:{const:'observe'},name:str,valueType}),
  obj({target:worldTarget,region:{const:'property'},name:str,valueType})]};
const assertion=obj({kind:{const:'assertion'},read:def('read'),quantifier:{enum:['one','any','all','none']},operator:{enum:['equals','notEquals','greaterThan','greaterThanOrEqual','lessThan','lessThanOrEqual','approximatelyEquals','contains','startsWith','endsWith','matches','notContains','containsAll','containsAny','isEmpty','isNotEmpty','countEquals','countNotEquals','countGreaterThan','countGreaterThanOrEqual','countLessThan','countLessThanOrEqual','sequenceEquals','startsWithSequence','endsWithSequence','containsSequence']},expected:gua('value-v1'),tolerance:{type:'number',minimum:0}},['kind','read','quantifier','operator']);
const condition={oneOf:[assertion,
  obj({kind:{const:'targets'},target,operator:{enum:['exists','notExists','countEquals','countNotEquals','countGreaterThan','countGreaterThanOrEqual','countLessThan','countLessThanOrEqual']},count:integer()},['kind','target','operator']),
  obj({kind:{enum:['all','any']},conditions:arr(def('condition'),1,100)}),
  obj({kind:{const:'time'},condition:def('condition'),withinMilliseconds:integer(0,86400000),forMilliseconds:integer(0,86400000)},['kind','condition'])]};
const fixedFile=obj({path:str,sha256:{type:'string',pattern:'^[0-9a-f]{64}$'}});
const limits=obj(Object.fromEntries(['maxDurationMilliseconds','maxActions','maxDecisions','prepareTimeoutMilliseconds','cleanupTimeoutMilliseconds','plannerTimeoutMilliseconds','waitTimeoutMilliseconds','maxSegmentMilliseconds','maxLatenessMilliseconds','maxObservationNodes','maxObservationBytes','regexTimeoutMilliseconds','regexMaxPatternLength','stagnationActionLimit','stagnationRepeatLimit','recoveryDecisionLimit','traceMaxBytes','traceMaxEvents','traceQueueCapacity','traceMaxAttachmentBytes'].map(k=>[k,integer(1,k==='maxSegmentMilliseconds'||k==='maxLatenessMilliseconds'?60000:2147483647)])));
limits.properties.traceRecentSteps=integer(1,100000); limits.properties.traceRecentSteps.default=100;
limits.properties.maxLatenessMilliseconds=integer(0,60000);
const secret=obj({secretKey:id});
const value={oneOf:[{type:'boolean'},{type:'number'},{type:'string',maxLength:4096},obj({x:{type:'number'},y:{type:'number'}})]};
const action={oneOf:[
  obj({kind:{const:'ui'},selector:gua('selector'),operation:{enum:['click','focus','set_value','set_checked','select','scroll','press_key']},value,secret},['kind','selector','operation']),
  obj({kind:{const:'semantic'},actionId:id,operation:{enum:['press','set','release']},value,secret},['kind','actionId','operation']),
  obj({kind:{const:'raw'},input:{$ref:'https://gua.dev/schema/timed-segment-v1.schema.json#/$defs/input'}})]};
const identity=obj({scenarioId:id,definitionVersion:id,scenarioSha256:fixedFile.properties.sha256,buildId:id,environmentSha256:fixedFile.properties.sha256});
save('common',{$defs:{id,read,target,valueType,condition,fixedFile,limits,secret,action,identity}});
save('scenario',obj({...header('scenario'),scenarioId:id,definitionVersion:id,name:str,goal:obj({objective:str,success:def('condition'),failure:def('condition')},['objective']),setup:def('condition'),constraints:obj({maxDurationMilliseconds:integer(1,86400000),maxActions:integer(0,2147483647),allowedActionIds:arr(id,0,1000)},['maxDurationMilliseconds','maxActions'])},['kind','schemaVersion','scenarioId','definitionVersion','name','goal','constraints']));
save('environment',obj({...header('environment'),environmentId:id,buildId:id,connection:obj({endpoint:{type:'string',pattern:'^wss?://[^\\s]+$',maxLength:4096},credential:def('secret')},['endpoint']),profile:{enum:['Player','Testing','Debug']},limits:def('limits'),permissions:obj({actionIds:arr(id),rawInput:{type:'boolean'},reads:arr(def('read'))}),fixture:obj({fixtureId:id}),trace:obj({captureMode:{enum:['recent','streaming']},savePolicy:{enum:['onFailure','always']}})},['kind','schemaVersion','environmentId','buildId','connection','profile','limits','permissions','trace']));
save('replay-plan',obj({...header('replayPlan'),planId:id,scenario:def('fixedFile'),recording:def('fixedFile'),timing:{enum:['recorded','conditionSynchronized']},completion:{enum:['onGoal','afterPlan']},checkpoints:arr(obj({beforeStep:integer(),condition:def('condition'),timeoutMilliseconds:integer(1,86400000)})),initial:def('condition')},['kind','schemaVersion','planId','scenario','recording','timing','completion','checkpoints']));
const correlation={runId:id,decisionRequestId:id,basedOnObservationId:id};
const branch=(kind,p,required)=>obj({...header('plannerDecision'),...correlation,decision:obj({kind:{const:kind},...p},['kind',...(required??Object.keys(p))])});
save('planner-decision',{oneOf:[branch('execute',{mode:{const:'single'},action:def('action')}),branch('execute',{mode:{const:'timed'},segment:gua('timed-segment-v1')}),branch('observe',{reads:arr(def('read'),1,100)}),branch('wait',{durationMilliseconds:integer(1,86400000)}),branch('wait',{condition:def('condition'),timeoutMilliseconds:integer(1,86400000)}),branch('finish',{report:{enum:['goalClaimed','stuck','cannotProceed']}})]});
const observation=obj({observationId:id,sourceId:id,sessionEpoch:integer(1),profile:{enum:['Player','Testing','Debug']},revision:integer(),complete:{type:'boolean'},reads:arr(obj({read:def('read'),status:{enum:['available','unavailable','omitted','truncated','stale','gap']},value:gua('value-v1')},['read','status']))});
save('planner-input',obj({...header('plannerInput'),...correlation,objective:str,limits:def('limits'),remaining:obj({actions:integer(),decisions:integer(),durationMilliseconds:integer()}),observation,actionDefinitions:{oneOf:[gua('game-input-actions'),gua('game-input-actions-v2')]},feedback:arr(obj({code:id,relatedDecisionRequestId:id},['code']))}));
save('run',obj({...header('run'),runId:id,identity:def('identity'),mode:{enum:['Explore','Replay']},executionState:{enum:['Created','Preparing','Running','Completing','Finished']},startedAt:{type:'string',format:'date-time'},scenario:def('fixedFile'),environment:def('fixedFile'),plan:def('fixedFile')},['kind','schemaVersion','runId','identity','mode','executionState','startedAt','scenario','environment']));
save('result',obj({...header('result'),runId:id,status:{enum:['Passed','Failed','TimedOut','Aborted','Invalid','Unverified']},phase:id,origin:id,reason:id,postProcessing:obj({complete:{type:'boolean'},reasons:arr(id)}),finishedAt:{type:'string',format:'date-time'} }));
save('scenario-registry',obj({...header('scenarioRegistry'),registryId:id,scenarios:arr(obj({scenarioId:id,definitionVersion:id,file:def('fixedFile')})),artifactRoots:arr(str,1,100)}));
save('adoption-evidence',obj({...header('adoptionEvidence'),plan:def('fixedFile'),scenario:def('fixedFile'),recording:def('fixedFile'),testRunId:id,identity:def('identity'),completion:{const:'afterPlan'},planCompleted:{const:true},goalVerified:{const:true},postProcessingComplete:{const:true},aiUsed:{const:false}}));
