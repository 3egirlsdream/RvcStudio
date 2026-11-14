"""Upload only immutable ciphertext/media. No raw checkpoints, indices or key files can enter the public repo."""
import argparse
import hashlib
import os
import re
from pathlib import Path

ALLOWED={'content.rvcm','cover.png','cover.jpg','preview.wav','preview.mp3','preview.m4a','manifest.json','item.json'}

def matches(path, previous):
    size=path.stat().st_size
    if previous.size!=size: return False
    sha256=hashlib.sha256(); blob=hashlib.sha1(f'blob {size}\0'.encode())
    with path.open('rb') as source:
        for block in iter(lambda:source.read(4194304), b''):
            sha256.update(block); blob.update(block)
    lfs=getattr(previous,'lfs',None)
    sha=lfs.get('sha256') if isinstance(lfs,dict) else getattr(lfs,'sha256',None)
    return sha==sha256.hexdigest() if sha else getattr(previous,'blob_id','')==blob.hexdigest()

def upload(api, repo, folder, prefix):
    from huggingface_hub import CommitOperationAdd
    if not re.fullmatch(r'store/models/[A-Za-z0-9-]{1,64}/[A-Za-z0-9-]{1,64}',prefix):
        raise ValueError('Invalid store publication prefix')
    files=list(folder.iterdir())
    if not files or any(not p.is_file() or p.name not in ALLOWED or p.is_symlink() for p in files):
        raise ValueError('Only encrypted packages and approved media may be published')
    encrypted=folder/'content.rvcm'
    if encrypted.exists():
        with encrypted.open('rb') as stream:
            if stream.read(6)!=b'RVCS1\n': raise ValueError('Invalid encrypted package')
    metadata={s.rfilename:s for s in api.model_info(repo,files_metadata=True).siblings}
    operations=[]
    for path in files:
        remote=f'{prefix}/{path.name}'
        previous=metadata.get(remote)
        if previous:
            if not matches(path,previous): raise ValueError('Published store versions are immutable')
        else: operations.append(CommitOperationAdd(path_in_repo=remote,path_or_fileobj=path))
    if operations: api.create_commit(repo_id=repo,operations=operations,commit_message=f'Publish store revision {prefix}')
    metadata={s.rfilename:s for s in api.model_info(repo,files_metadata=True).siblings}
    for path in files:
        sibling=metadata.get(f'{prefix}/{path.name}')
        if sibling is None or not matches(path,sibling): raise ValueError('Remote verification failed')

if __name__=='__main__':
    from huggingface_hub import HfApi
    parser=argparse.ArgumentParser(); parser.add_argument('--repository',required=True); parser.add_argument('--folder',required=True,type=Path); parser.add_argument('--prefix',required=True)
    args=parser.parse_args()
    if not args.prefix.startswith('store/models/') or '..' in args.prefix or not os.environ.get('HF_TOKEN'): raise ValueError('Invalid store publication configuration')
    upload(HfApi(token=os.environ['HF_TOKEN']),args.repository,args.folder,args.prefix)
