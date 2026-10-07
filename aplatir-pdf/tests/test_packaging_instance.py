# -*- coding: utf-8 -*-
"""Audit « packaging » : protocole d'instance unique (socket local).

Hermétique : on remplace le port fixe par un port libre choisi par l'OS et on
isole le profil (APPDATA / nom d'utilisateur) dans tmp_path.
"""
from __future__ import annotations

import socket
import threading

import pytest

tray = pytest.importorskip("aplatir_tray")


def _port_libre() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@pytest.fixture
def port(monkeypatch):
    p = _port_libre()
    monkeypatch.setattr(tray, "PORT_INSTANCE", p, raising=False)
    return p


def test_message_fichiers_aller_retour(port):
    """Une 2e instance transmet les chemins (espaces, accents) à la 1re et reçoit « OK »."""
    recus: list = []
    serveur = tray.devenir_instance_principale()
    assert serveur is not None
    t = threading.Thread(target=tray.servir_instance, args=(serveur, recus.append), daemon=True)
    t.start()
    try:
        msg = {"cmd": "files",
               "paths": [r"C:\Users\José Müller\Rapports fin fab\essai é.pdf", "/tmp/dossier a b/c.pdf"]}
        assert tray.prevenir_instance_existante(msg) is True
    finally:
        serveur.close()
    t.join(5)
    assert recus == [msg]


def test_pas_de_serveur_retourne_faux_vite(port):
    """Sans instance en cours, la tentative de réveil échoue proprement (pas d'exception, pas de blocage)."""
    assert tray.prevenir_instance_existante({"cmd": "show"}) is False


@pytest.mark.xfail(strict=True, reason="PKG-1: le port 127.0.0.1 est commun à toute la machine : "
                                       "l'instance du premier utilisateur/session répond pour tous les autres")
def test_deux_profils_utilisateur_ont_chacun_leur_instance(port, monkeypatch, tmp_path):
    serveurs = []
    try:
        for nom in ("alice", "bob"):
            monkeypatch.setenv("APPDATA", str(tmp_path / nom))
            for var in ("USERNAME", "USER", "LOGNAME"):
                monkeypatch.setenv(var, nom)
            serveurs.append(tray.devenir_instance_principale())
        assert all(s is not None for s in serveurs), (
            "le 2e profil n'a pas pu devenir instance principale : il se contenterait de réveiller "
            "(et d'envoyer ses fichiers à) l'instance du 1er profil")
    finally:
        for s in serveurs:
            if s is not None:
                s.close()
