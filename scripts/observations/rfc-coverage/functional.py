#!/usr/bin/env python3
"""Exercise the live catalog, MRTR and authoritative fixture postconditions."""
import argparse
import asyncio
import json
from pathlib import Path
import secrets
import urllib.parse
import xml.etree.ElementTree as ET
from driver import Client, environment, query
from infra import request, resource, verify

SAFE_STRINGS = {'outcome','code','category','phase','state','mutationState','resultType','kind','entityKind',
                'resultKind','completionState','mode','source','timeZone','operation','field','action',
                'protocolVersion','supportedVersions'}


def sanitize(value, key=''):
    if isinstance(value,dict):
        return {k:sanitize(v,k) for k,v in value.items()}
    if isinstance(value,list):
        return [sanitize(v,key) for v in value]
    if isinstance(value,str) and key not in SAFE_STRINGS:
        return '[redacted]'
    return value


class Functional:
    def __init__(self,root,client,state):
        self.root,self.client,self.state=root,client,state
        self.records=[]
        self.owned=set()
        self.phase='scenario'

    def save(self):
        (self.root/'functional.json').write_text(json.dumps(self.records,indent=2))

    async def call(self,tool,args,_round=0,_expected='success',**continuation):
        if _round > 2:
            raise RuntimeError(f'{tool}: more than two MRTR continuations')
        response,record=await self.client.call(tool,args,**continuation)
        record.update(driver='direct_mcp', continuation_round=_round, expected_outcome=_expected, phase=self.phase,
                      structured=sanitize(response.get('result',{})))
        self.records.append(record)
        self.save()
        result=response.get('result',{})
        if result.get('resultType')=='input_required':
            answers={key:dict(action='accept',content={name:True for name,prop in value['params']['requestedSchema']['properties'].items()
                         if prop.get('type')=='boolean'}) for key,value in result['inputRequests'].items()}
            return await self.call(tool,args,_round=_round+1,_expected=_expected,
                                   requestState=result['requestState'],inputResponses=answers)
        structured=result.get('structuredContent',{})
        expected={_expected} if isinstance(_expected,str) else set(_expected)
        actual=structured.get('outcome','') if structured.get('outcome')=='success' else structured.get('code')
        if actual not in expected:
            raise RuntimeError(f'{tool}: '+json.dumps(sanitize(response)))
        print(tool+' '+actual,flush=True)
        return structured

    async def read(self,href):
        return (await self.call('calendar_resources.get',dict(href=href)))['snapshot']

    def href(self, path):
        return self.state['url'] + self.state['home_path'] + path

    def authoritative(self,href,contains=None,absent=False):
        status,data,_=request(self.state,'GET',urllib.parse.urlparse(href).path)
        assert status==(404 if absent else 200), status
        if contains:
            assert contains.encode() in data
        self.records.append(dict(authoritative=True,status=status,postcondition=True))
        self.save()

    def collection_absent(self, href):
        body='<D:propfind xmlns:D="DAV:"><D:allprop/></D:propfind>'
        status,data,_=request(self.state,'PROPFIND',urllib.parse.urlparse(href).path,body,{'Depth':'0'})
        # Some servers keep a trash node at the former href. It must cease to be a CalDAV Calendar.
        is_calendar=status==207 and ET.fromstring(data).find('.//{DAV:}resourcetype/{urn:ietf:params:xml:ns:caldav}calendar') is not None
        assert status==404 or (status==207 and not is_calendar), dict(status=status,is_calendar=is_calendar)
        self.records.append(dict(authoritative=True,status=status,postcondition=True,
                                 collection_no_longer_calendar=True))
        self.save()

    async def run(self):
        catalog=(await self.client.request('tools/list',{}))['result']['tools']
        (self.root/'catalog-exact-live.json').write_text(json.dumps(catalog,indent=2))
        await self.call('calendars.list',{})
        for tool in ['calendar_entities.query','calendar_occurrences.query','todos.query']:
            for size in [1,5,200]:
                result=await self.call(tool,query(tool,size))
                cursor=result['pagination'].get('nextCursor')
                if cursor:
                    await self.call(tool,dict(cursor=cursor,pageSize=size))
        for tool in ['calendar_entities.query','todos.query']:
            await self.call(tool,query(tool,5,bounded=False))
        href=self.href('todos/0001.ics')
        await self.read(href)
        await self.call('calendar_resources.exact_get',dict(href=href))
        for kind in ['event','todo']:
            fields=dict(summary='Disposable functional '+kind,
                        start=dict(kind='utcDateTime',value='2026-09-05T12:00:00Z'))
            if kind=='event':
                fields.update(end=dict(kind='utcDateTime',value='2026-09-05T13:00:00Z'),
                              recurrenceSet=dict(rrule='FREQ=DAILY;COUNT=3'))
            result=await self.call(kind+'s.create',dict(destination=dict(mode='default'),
                                  entity=dict(kind=kind,uid='functional-'+kind+'-'+secrets.token_hex(4),fields=fields)))
            snapshot=result['snapshot']
            href=snapshot['resourceRevision']['href'];self.owned.add(href)
            self.authoritative(href,'SUMMARY:Disposable functional '+kind)
            snapshot=await self.read(href)
            await self.call(kind+'s.patch',dict(snapshot=snapshot['entityRevision'],target=dict(scope='master'),
                patch=dict(scalars=[dict(field='summary',operation='set',value='Patched functional '+kind)])))
            self.authoritative(href,'SUMMARY:Patched functional '+kind)
            if kind=='event':
                identity=dict(value=dict(kind='utcDateTime',value='2026-09-09T12:00:00Z'))
                for verb in ['add','exclude','restore_exclusion','cancel','restore_cancellation']:
                    snapshot=await self.read(href)
                    await self.call('calendar_occurrences.'+verb,dict(snapshot=snapshot['entityRevision'],recurrenceIdentity=identity))
                    self.authoritative(href)
            else:
                snapshot=await self.read(href)
                await self.call('todos.complete',dict(snapshot=snapshot['entityRevision']))
                self.authoritative(href,'STATUS:COMPLETED')
                for destination in ['archive','todos']:
                    snapshot=await self.read(href)
                    moved=await self.call('calendar_resources.move',dict(revision=snapshot['entityRevision'],
                        destination=dict(mode='selected',calendar=dict(by='href',href=self.href(destination+'/')))),
                        _expected='success' if self.state.get('profile') else 'unsupported_capability')
                    if not self.state.get('profile'):
                        self.authoritative(href,'STATUS:COMPLETED')
                        break
                    self.authoritative(href,absent=True);self.owned.discard(href)
                    href=moved['snapshot']['resourceRevision']['href'];self.owned.add(href)
                    self.authoritative(href,'STATUS:COMPLETED')
            snapshot=await self.read(href)
            await self.call('calendar_resources.delete',dict(revision=snapshot['entityRevision']))
            self.authoritative(href,absent=True);self.owned.discard(href)
        href=self.href('events/exact-functional.ics')
        data=resource('VEVENT',9001)
        self.owned.add(href)
        await self.call('calendar_resources.exact_create',dict(destinationHref=href,utf8Resource=data))
        self.authoritative(href)
        snapshot=await self.read(href)
        await self.call('calendar_resources.exact_replace',dict(revision=snapshot['entityRevision'],
                        utf8Resource=data.replace('RFC fixture VEVENT 9001','Exact replaced')))
        self.authoritative(href,'SUMMARY:Exact replaced')
        snapshot=await self.read(href)
        destination=href.replace('exact-functional','exact-moved')
        self.owned.add(destination)
        await self.call('calendar_resources.exact_move',dict(revision=snapshot['entityRevision'],destinationHref=destination),
                        _expected='success' if self.state.get('profile') else 'unsupported_capability')
        if self.state.get('profile'):
            self.authoritative(href,absent=True);self.owned.discard(href)
        else:
            self.authoritative(destination,absent=True);self.owned.discard(destination)
            destination=href
        snapshot=await self.read(destination)
        await self.call('calendar_resources.delete',dict(revision=snapshot['entityRevision']))
        self.authoritative(destination,absent=True);self.owned.discard(destination)
        collection=self.href(self.state.get('functional_calendar_path','functional-calendar-'+secrets.token_hex(4)+'/'))
        self.owned.add(collection)
        await self.call('calendars.create',dict(displayName='Disposable functional calendar',
                        entityKinds=['event','todo'],destinationHref=collection))
        new_catalog=any(tool['name']=='calendars.inspect' for tool in catalog)
        collection_delete='unsupported_capability' if new_catalog and self.state.get('server') in ('baikal','nextcloud') else 'success'
        deletion=await self.call('calendars.delete',dict(href=collection),_expected=collection_delete)
        if collection_delete=='success':
            self.collection_absent(collection);self.owned.discard(collection)
        else:
            assert deletion['mutationState']=='not_attempted'
            self.authoritative(collection)
        if new_catalog:
            from protocol import new_operations
            await new_operations(self)
        called={r['tool'] for r in self.records if 'tool' in r}
        expected={t['name'] for t in catalog}
        assert called==expected, dict(missing=sorted(expected-called),unexpected=sorted(called-expected))
        summary=dict(catalog_tools=len(expected),called_tools=sorted(called),
                     calls=sum('tool' in r for r in self.records),
                     authoritative_checks=sum(r.get('authoritative',False) for r in self.records))
        (self.root/'summary.json').write_text(json.dumps(summary,indent=2))
        print(json.dumps(summary),flush=True)


async def run(root,assembly,output,exact_scope=False):
    output.mkdir(parents=True,exist_ok=False)
    state=json.loads((root/'infra-private.json').read_text())
    if exact_scope:
        configure_scope(state)
    before=verify(state)
    service='caldav-rfc-'+state.get('server','radicale')+'-'+output.name
    client=Client(assembly,environment(state,service,exact=True))
    try:
        async with client:
            f=Functional(output,client,state)
            try:
                await f.run()
            finally:
                for href in f.owned:
                    status,_,_=request(state,'DELETE',urllib.parse.urlparse(href).path)
                    assert status in (200,204,404)
    finally:
        if hasattr(client,'identity'):
            client.identity['service_name']=service
            client.identity['explicit_calendar_scope']=exact_scope
            (output/'functional-process.json').write_text(json.dumps(client.identity,indent=2))
    assert verify(state)==before, 'Smoke suite changed the seeded fixture resource counts'
    if getattr(f,'new_checkpoint',None):
        from protocol import checkpoint_restart
        await checkpoint_restart(f.new_checkpoint,assembly,environment(state,service),output/'checkpoint-restart')


def configure_scope(state):
    state['functional_calendar_path']='functional-calendar-'+secrets.token_hex(4)+'/'
    state['protocol_calendar_path']='native-operations-'+secrets.token_hex(4)+'/'
    state['calendar_hrefs']=[state['url']+state['home_path']+path for path in
        ['events/','todos/','archive/',state['functional_calendar_path'],state['protocol_calendar_path']]]


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('assembly',type=Path)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--exact-scope',action='store_true')
    a=p.parse_args();asyncio.run(run(a.root,a.assembly,a.output,a.exact_scope))
