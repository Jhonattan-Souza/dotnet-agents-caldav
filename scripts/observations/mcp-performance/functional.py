#!/usr/bin/env python3
"""Exercise the live catalog, MRTR and authoritative fixture postconditions."""
import argparse
import asyncio
import json
from pathlib import Path
import secrets
import urllib.parse
from driver import Client, environment, query
from infra import request, resource, verify, expected_counts

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

    def save(self):
        (self.root/'functional.json').write_text(json.dumps(self.records,indent=2))

    async def call(self,tool,args,**continuation):
        response,record=await self.client.call(tool,args,**continuation)
        record.update(driver='direct_mcp', structured=sanitize(response.get('result',{})))
        self.records.append(record)
        self.save()
        result=response.get('result',{})
        if result.get('resultType')=='input_required':
            answers={key:dict(action='accept',content={name:True for name,prop in value['params']['requestedSchema']['properties'].items()
                         if prop.get('type')=='boolean'}) for key,value in result['inputRequests'].items()}
            return await self.call(tool,args,requestState=result['requestState'],inputResponses=answers)
        structured=result.get('structuredContent',{})
        if structured.get('outcome')!='success':
            raise RuntimeError(f'{tool}: '+json.dumps(sanitize(response)))
        print(tool+' success',flush=True)
        return structured

    async def read(self,href):
        return (await self.call('calendar_resources.get',dict(href=href)))['snapshot']

    def authoritative(self,href,contains=None,absent=False):
        status,data,_=request(self.state,'GET',urllib.parse.urlparse(href).path)
        assert status==(404 if absent else 200), status
        if contains:
            assert contains.encode() in data
        self.records.append(dict(authoritative=True,status=status,postcondition=True))
        self.save()

    async def run(self):
        catalog=(await self.client.request('tools/list',{}))['result']['tools']
        (self.root/'catalog-exact-live.json').write_text(json.dumps(catalog,indent=2))
        await self.call('calendars.list',{})
        for tool in ['calendar_entities.query','calendar_occurrences.query','todos.query']:
            for size in [1,5,200]:
                result=await self.call(tool,query(tool,size))
                if result['pagination']['nextCursor'] is not None:
                    await self.call(tool,dict(cursor=result['pagination']['nextCursor'],pageSize=size))
        for tool in ['calendar_entities.query','todos.query']:
            await self.call(tool,query(tool,5,bounded=False))
        href=self.state['url']+'/perftest/todos/0001.ics'
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
            patched=await self.call(kind+'s.patch',dict(snapshot=snapshot['entityRevision'],target=dict(scope='master'),
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
                        destination=dict(mode='selected',calendar=dict(by='href',href=self.state['url']+'/perftest/'+destination+'/'))))
                    self.authoritative(href,absent=True);self.owned.discard(href)
                    href=moved['snapshot']['resourceRevision']['href'];self.owned.add(href)
                    self.authoritative(href,'STATUS:COMPLETED')
            snapshot=await self.read(href)
            await self.call('calendar_resources.delete',dict(revision=snapshot['entityRevision']))
            self.authoritative(href,absent=True);self.owned.discard(href)
        href=self.state['url']+'/perftest/events/exact-functional.ics'
        data=resource('VEVENT',9001)
        self.owned.add(href)
        await self.call('calendar_resources.exact_create',dict(destinationHref=href,utf8Resource=data))
        self.authoritative(href)
        snapshot=await self.read(href)
        await self.call('calendar_resources.exact_replace',dict(revision=snapshot['entityRevision'],
                        utf8Resource=data.replace('Performance VEVENT 9001','Exact replaced')))
        self.authoritative(href,'SUMMARY:Exact replaced')
        snapshot=await self.read(href)
        destination=href.replace('exact-functional','exact-moved')
        self.owned.add(destination)
        await self.call('calendar_resources.exact_move',dict(revision=snapshot['entityRevision'],destinationHref=destination))
        self.authoritative(href,absent=True);self.owned.discard(href)
        snapshot=await self.read(destination)
        await self.call('calendar_resources.delete',dict(revision=snapshot['entityRevision']))
        self.authoritative(destination,absent=True);self.owned.discard(destination)
        collection=self.state['url']+'/perftest/functional-calendar/'
        self.owned.add(collection)
        await self.call('calendars.create',dict(displayName='Disposable functional calendar',
                        entityKinds=['event','todo'],destinationHref=collection))
        await self.call('calendars.delete',dict(href=collection))
        self.authoritative(collection,absent=True);self.owned.discard(collection)
        called={r['tool'] for r in self.records if 'tool' in r}
        assert called=={t['name'] for t in catalog},called
        assert verify(self.state)==expected_counts(self.root)


async def run(root,assembly):
    if expected_counts(root)['todos']<2:
        raise RuntimeError('The functional matrix requires at least two seeded todos; cleanup supports any seed count')
    state=json.loads((root/'infra-private.json').read_text())
    async with Client(assembly,environment(state,'caldav-perf-functional',exact=True)) as client:
        f=Functional(root,client,state)
        try:
            await f.run()
        finally:
            for href in f.owned:
                status,_,_=request(state,'DELETE',urllib.parse.urlparse(href).path)
                assert status in (200,204,404)
    (root/'functional-process.json').write_text(json.dumps(client.identity,indent=2))


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('root',type=Path);p.add_argument('assembly',type=Path)
    a=p.parse_args();asyncio.run(run(a.root,a.assembly))
