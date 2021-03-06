using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Shielded;

namespace ConsoleTests
{
    public struct Event
    {
        public int Id;
        public string HomeTeam;
        public string AwayTeam;
        public ShieldedSeq<Shielded<BetOffer>> BetOffers;
    }

    public struct BetOffer
    {
        public int Id;
        public Shielded<Event> Event;
        public string Pick;
        public decimal Odds;
    }

    public struct Ticket
    {
        public int Id;
        public decimal PayInAmount;
        public decimal WinAmount;
        // readonly
        public Bet[] Bets;
    }

    public struct Bet
    {
        public Shielded<BetOffer> Offer;
        public decimal Odds;
    }

    public class BetShop
    {
        public int TicketCount
        {
            get
            {
                return TicketIdSeq.Count;
            }
        }

        public readonly ShieldedDict<int, Shielded<Event>> Events;

        private int _ticketIdGenerator = 0;
        public readonly ShieldedSeq<int> TicketIdSeq = new ShieldedSeq<int>();
        public readonly ShieldedDict<int, Shielded<Ticket>> Tickets =
            new ShieldedDict<int, Shielded<Ticket>>();
        private ShieldedDict<string, decimal> _sameTicketWins =
            new ShieldedDict<string, decimal>();

        public decimal SameTicketWinLimit = 1000m;

        static string GetOfferHash(Shielded<Ticket> newTicket)
        {
            return newTicket.Read.Bets.Select(b => b.Offer.Read.Id)
                .OrderBy(id => id)
                .Aggregate(new StringBuilder(), (sb, next) => 
                {
                    if (sb.Length > 0)
                        sb.Append(",");
                    sb.Append(next);
                    return sb;
                }, sb => sb.ToString());
        }

        public int? BuyTicket(decimal payIn, params Shielded<BetOffer>[] bets)
        {
            var newId = Interlocked.Increment(ref _ticketIdGenerator);
            var newTicket = new Shielded<Ticket>(new Ticket()
            {
                Id = newId,
                PayInAmount = payIn
            });
            return Shield.InTransaction(() =>
            {
                newTicket.Modify((ref Ticket t) =>
                {
                    t.Bets = bets.Select(shBo => new Bet()
                        {
                            Offer = shBo,
                            Odds = shBo.Read.Odds
                        }).ToArray();
                    t.WinAmount = t.PayInAmount *
                        t.Bets.Aggregate(1m, (curr, nextBet) => curr * nextBet.Odds);
                });

                var hash = GetOfferHash(newTicket);
                if (_sameTicketWins[hash] + newTicket.Read.WinAmount > SameTicketWinLimit)
                    return false;

                Tickets[newId] = newTicket;
                TicketIdSeq.Append(newId);
                _sameTicketWins[hash] = _sameTicketWins[hash] + newTicket.Read.WinAmount;
                return true;
            }) ? (int?)newId : null;
        }

        /// <summary>
        /// Creates n events, with three typical offers (1,X,2) for each.
        /// The events get IDs 1-n.
        /// </summary>
        public BetShop(int n)
        {
            List<Shielded<Event>> initialEvents = new List<Shielded<Event>>();

            Shield.InTransaction(() =>
            {
                int eventIdGenerator = 1;
                int offerIdGenerator = 1;
                for (int i = 0; i < n; i++)
                {
                    var newEvent = new Shielded<Event>(new Event()
                    {
                        Id = eventIdGenerator++,
                        HomeTeam = "Home " + i,
                        AwayTeam = "Away " + i
                    });
                    // we have to use Modify, because each offer needs a ref to the shielded
                    // event, which we do not have before that shielded event is constructed. And,
                    // after he is constructed, he can only be changed like this.
                    newEvent.Modify((ref Event e) =>
                        e.BetOffers = new ShieldedSeq<Shielded<BetOffer>>(
                            new Shielded<BetOffer>(new BetOffer()
                            {
                                Id = offerIdGenerator++,
                                Event = newEvent,
                                Pick = "1",
                                Odds = 2m
                            }),
                            new Shielded<BetOffer>(new BetOffer()
                            {
                                Id = offerIdGenerator++,
                                Event = newEvent,
                                Pick = "X",
                                Odds = 4m
                            }),
                            new Shielded<BetOffer>(new BetOffer()
                            {
                                Id = offerIdGenerator++,
                                Event = newEvent,
                                Pick = "2",
                                Odds = 4.5m
                            })));
                    initialEvents.Add(newEvent);
                }
            });

            Events = new ShieldedDict<int, Shielded<Event>>(
                initialEvents.Select(e => new KeyValuePair<int, Shielded<Event>>(e.Read.Id, e)));
        }

        /// <summary>
        /// Checks if the rule about limiting same tickets was violated.
        /// </summary>
        public bool VerifyTickets(out int ticketCount)
        {
            int count = 0;
            bool result = false;
            Shield.InTransaction(() =>
            {
                Dictionary<string, decimal> checkTable = new Dictionary<string, decimal>();
                count = 0;
                foreach (var id in TicketIdSeq)
                {
                    var t = Tickets[id];
                    var hash = GetOfferHash(t);
                    if (!checkTable.ContainsKey(hash))
                        checkTable[hash] = t.Read.WinAmount;
                    else
                        checkTable[hash] = checkTable[hash] + t.Read.WinAmount;
                    if (checkTable[hash] > SameTicketWinLimit)
                    {
                        result = false;
                        return;
                    }
                    count++;
                }
                result = true;
            });
            ticketCount = count;
            return result;
        }
    }
}
