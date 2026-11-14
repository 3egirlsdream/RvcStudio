"""Publish server-encrypted store drafts from a network-reachable Actions runner.

The scoped publisher credential can fetch approved ciphertext/media and acknowledge
their hashes. It cannot fetch original models, wrapped keys, account data or licenses.
"""
import hashlib
import os
from pathlib import Path
import re
import tempfile

import requests
from huggingface_hub import HfApi
from store_publish_transport import ALLOWED, upload

BASE='https://thankful.top/api/RvcStore/'

def main():
    credential=os.environ['STORE_PUBLISHER_KEY'].strip()
    if len(credential)<40: raise RuntimeError('Store publisher credential is missing')
    session=requests.Session();session.headers['Authorization']='Bearer '+credential
    hub=HfApi(token=os.environ['HF_TOKEN'])
    def call(action,body=None,params=None):
        response=session.post(BASE+action,json=body or {},params=params,timeout=(15,120))
        response.raise_for_status();data=response.json()
        if data.get('success') is not True: raise RuntimeError('Store publication service rejected request')
        return data.get('data')
    for _ in range(20):
        job=call('PublisherNext')
        if not job: print('No pending encrypted store drafts.');return
        for name in ('jobId','modelId','revision','lease'):
            if not re.fullmatch(r'[A-Za-z0-9-]{1,64}',job[name]): raise ValueError('Invalid publication identifier')
        print('Publishing encrypted draft '+job['jobId'],flush=True)
        try:
            report=[]
            with tempfile.TemporaryDirectory(prefix='rvc-encrypted-store-') as temporary:
                folder=Path(temporary);total=0
                for metadata in job['files']:
                    metadata={k[0].lower()+k[1:]:v for k,v in metadata.items()}
                    name,size=metadata['name'],metadata['size'];total+=size
                    if name not in ALLOWED or not 0<size<=2147483648 or total>2200000000: raise ValueError('Unapproved publication file')
                    digest=hashlib.sha256();received=0
                    with session.get(BASE+'PublisherFile',params={'id':job['jobId'],'lease':job['lease'],'name':name},stream=True,timeout=(15,120)) as response:
                        response.raise_for_status()
                        with (folder/name).open('wb') as target:
                            for block in response.iter_content(1048576):
                                received+=len(block)
                                if received>size: raise ValueError('Encrypted publication exceeded expected size')
                                digest.update(block);target.write(block)
                    if received!=size or digest.hexdigest().upper()!=metadata['sha256'].upper(): raise ValueError('Encrypted publication hash mismatch')
                    report.append({'name':name,'size':size,'sha256':digest.hexdigest().upper()})
                upload(hub,'xulezuo/RvcStudio',folder,'store/models/'+job['modelId']+'/'+job['revision'])
                call('PublisherComplete',{'jobId':job['jobId'],'lease':job['lease'],'files':report})
            print('Verified and completed encrypted draft '+job['jobId'],flush=True)
        except Exception as error:
            try:call('PublisherRelease',params={'id':job['jobId'],'lease':job['lease']})
            except Exception:pass
            # Provider exception text can contain signed URLs. Keep diagnostics scoped.
            raise RuntimeError('Encrypted draft publication failed: '+type(error).__name__) from None

if __name__=='__main__':main()
